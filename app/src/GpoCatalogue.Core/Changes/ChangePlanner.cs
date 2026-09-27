using GpoCatalogue.Core.Decode;
using GpoCatalogue.Core.Gpo;
using GpoCatalogue.Core.Live;
using GpoCatalogue.Core.Model;
using GpoCatalogue.Core.Pol;
using GpoCatalogue.Core.State;

namespace GpoCatalogue.Core.Changes;

public enum DiffKind
{
    Added,
    Removed,
    Changed,
}

/// <summary>One registry value a change touches.</summary>
public sealed record EntryDiff(DiffKind Kind, Scope Scope, PolEntry? Before, PolEntry? After)
{
    public string Line
    {
        get
        {
            var e = (After ?? Before)!;
            var where = $"{Scope.Hive()}\\{e.Key}!{e.Name}";
            return Kind switch
            {
                DiffKind.Added => $"+ {where}: {Effect(After!)}",
                DiffKind.Removed => $"- {where}: {Effect(Before!)}",
                _ => $"~ {where}: {Effect(Before!)}  ->  {Effect(After!)}",
            };
        }
    }

    private static string Effect(PolEntry e) => e.Marker switch
    {
        PolMarker.Del => "delete value",
        PolMarker.DelVals or PolMarker.DeleteValues => "clear key first",
        PolMarker.None => $"{e.TypeName} {e.Render()}" + (e.Soft ? " (only if missing)" : ""),
        _ => e.ValueName,
    };
}

/// <summary>A pending change with everything the review shows about it.</summary>
public sealed class PlannedChange
{
    public required PendingChange Change { get; init; }
    public required PolicyDef Policy { get; init; }
    public required string Current { get; init; }
    public required string New { get; init; }
    public required IReadOnlyList<EntryDiff> Diffs { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>False when the change cannot be written as it stands.</summary>
    public bool CanApply => !Change.NeedsInput;

    public Scope Scope => Change.Scope;
}

/// <summary>A policy that was not asked to change but will, because it shares registry values with one that was.</summary>
public sealed record SideEffect(PolicyDef Policy, Scope Scope, string Before, string After);

public sealed class ChangePlan
{
    public required IReadOnlyList<PlannedChange> Changes { get; init; }
    public required IReadOnlyList<SideEffect> SideEffects { get; init; }
    public required IReadOnlyDictionary<Scope, SectionChange> Sections { get; init; }

    /// <summary>Entries outside the catalogue that a restore adds or removes.</summary>
    public IReadOnlyList<EntryDiff> OtherDiffs { get; init; } = [];

    public bool HasWork => Sections.Count > 0;
}

/// <summary>Works out what applying a set of pending changes does to the policy object.</summary>
public sealed class ChangePlanner(CatalogStore catalog, PolicyStateService states)
{
    private readonly PolicyDecoder _decoder = states.Decoder;

    public ChangePlan Plan(IEnumerable<PendingChange> pending)
    {
        var included = pending.Where(c => !c.NeedsInput).ToList();
        var all = pending.ToList();
        var sections = new Dictionary<Scope, SectionChange>();
        var after = new Dictionary<Scope, List<PolEntry>>();

        foreach (var scope in new[] { Scope.Machine, Scope.User })
        {
            var current = states.Snapshot.Entries(scope);
            var entries = current.ToList();
            var extensions = new List<Guid>();
            foreach (var change in included.Where(c => c.Scope == scope))
            {
                entries = SectionState.Merge(
                    entries.Where(e => !catalog.Index.Owns(scope, e, change.Uid)), change.Target);
                if (Guid.TryParse(catalog[change.Uid].ClientExtension, out var extension))
                    extensions.Add(extension);
            }
            after[scope] = entries;
            if (!SameSequence(current, entries))
                sections[scope] = new SectionChange(current, entries, extensions.Distinct().ToList());
        }

        var planned = all.Select(c => Describe(c, after[c.Scope])).ToList();
        return new ChangePlan
        {
            Changes = planned,
            SideEffects = FindSideEffects(after, all),
            Sections = sections,
        };
    }

    /// <summary>The plan that makes the policy object equal to <paramref name="backup"/>.</summary>
    public ChangePlan PlanRestore(GpoSnapshot backup)
    {
        var sections = new Dictionary<Scope, SectionChange>();
        var changes = new List<PlannedChange>();
        var other = new List<EntryDiff>();
        foreach (var scope in new[] { Scope.Machine, Scope.User })
        {
            var current = states.Snapshot.Entries(scope);
            var target = backup.Entries(scope);
            if (SectionState.Equivalent(current, target))
                continue;
            sections[scope] = new SectionChange(current, target, []);

            foreach (var pol in catalog.Policies.Where(p => p.Scopes.Contains(scope)))
            {
                var mine = target.Where(e => catalog.Index.Owns(scope, e, pol.Uid)).ToList();
                var state = states[pol.Uid, scope];
                if (SectionState.Equivalent(state.Entries, mine))
                    continue;
                changes.Add(Describe(new PendingChange
                {
                    Uid = pol.Uid,
                    Scope = scope,
                    Source = ChangeSource.Restore,
                    Target = mine,
                    BaseHash = state.Hash,
                }, target));
            }
            other.AddRange(Diff(scope,
                current.Where(e => catalog.Index.Resolve(scope, e).Count == 0).ToList(),
                target.Where(e => catalog.Index.Resolve(scope, e).Count == 0).ToList()));
        }
        return new ChangePlan { Changes = changes, SideEffects = [], Sections = sections, OtherDiffs = other };
    }

    public static List<EntryDiff> Diff(Scope scope, IReadOnlyList<PolEntry> before, IReadOnlyList<PolEntry> after)
    {
        var old = SectionState.ToMap(before);
        var now = SectionState.ToMap(after);
        var diffs = new List<EntryDiff>();
        foreach (var (slot, e) in old)
        {
            if (!now.TryGetValue(slot, out var replacement))
                diffs.Add(new EntryDiff(DiffKind.Removed, scope, e, null));
            else if (!SectionState.SameEffect(e, replacement))
                diffs.Add(new EntryDiff(DiffKind.Changed, scope, e, replacement));
        }
        diffs.AddRange(now.Where(kv => !old.ContainsKey(kv.Key))
            .Select(kv => new EntryDiff(DiffKind.Added, scope, null, kv.Value)));
        return diffs;
    }

    private PlannedChange Describe(PendingChange change, IReadOnlyList<PolEntry> sectionAfter)
    {
        var pol = catalog[change.Uid];
        var state = states[change.Uid, change.Scope];
        var decoded = change.Target.Count > 0 ? _decoder.Decode(pol, change.Scope, change.Target) : null;
        var warnings = new List<string>();

        if (change.NeedsInput)
            warnings.Add("Needs input before it can be applied: " + string.Join(" ", change.Missing));
        if (change.TemplateDefaults)
            warnings.Add("The option values are the template's defaults. The model recommends the state, not the values; review them.");
        if (change.BaseHash.Length > 0 && change.BaseHash != state.Hash)
            warnings.Add("The policy was changed by something else after this change was staged.");
        var readsBack = decoded?.State ?? PolicyState.NotConfigured;
        if (change.Intended is { } intended && intended != readsBack && !change.NeedsInput)
        {
            // Some templates cannot tell two of their own states apart once written.
            warnings.Add($"Asked for {intended.Display()}, but what the template writes for it reads back as "
                         + $"{readsBack.Display()}.");
        }
        if (change.Target.Count == 0 && state.IsConfigured && LiveComparer.LeavesValuesBehind(pol))
        {
            warnings.Add("This policy writes outside the registry keys reserved for policy. Windows keeps the "
                         + "current value after the policy is set to Not configured; set it to Disabled or to an "
                         + "explicit value if the value itself has to change.");
        }
        if (state.Live.Status == LiveStatus.RegistryOnly && change.Target.Count == 0)
            warnings.Add("The registry value was not set through local group policy and will stay as it is.");

        return new PlannedChange
        {
            Change = change,
            Policy = pol,
            Current = state.Text,
            New = decoded?.Summary ?? PolicyState.NotConfigured.Display(),
            Diffs = Diff(change.Scope, state.Entries, change.Target),
            Warnings = warnings,
        };
    }

    private List<SideEffect> FindSideEffects(Dictionary<Scope, List<PolEntry>> after, List<PendingChange> pending)
    {
        var asked = pending.Select(c => (c.Uid, c.Scope)).ToHashSet();
        var effects = new List<SideEffect>();
        foreach (var (scope, entries) in after)
        {
            var current = states.Snapshot.Entries(scope);
            // Only policies owning a value that differs can be affected.
            var touched = Diff(scope, current, entries)
                .SelectMany(d => catalog.Index.Resolve(scope, (d.After ?? d.Before)!))
                .Select(h => h.Uid)
                .Distinct()
                .Where(uid => !asked.Contains((uid, scope)));
            foreach (var uid in touched)
            {
                var pol = catalog[uid];
                var before = states[uid, scope].Summary;
                var now = _decoder.Decode(pol, scope, entries)?.Summary ?? PolicyState.NotConfigured.Display();
                if (before != now)
                    effects.Add(new SideEffect(pol, scope, before, now));
            }
        }
        return effects;
    }

    private static bool SameSequence(IReadOnlyList<PolEntry> a, IReadOnlyList<PolEntry> b) =>
        a.Count == b.Count && a.Zip(b).All(p =>
            string.Equals(p.First.Key, p.Second.Key, StringComparison.OrdinalIgnoreCase)
            && string.Equals(p.First.ValueName, p.Second.ValueName, StringComparison.OrdinalIgnoreCase)
            && p.First.Type == p.Second.Type && p.First.Raw.AsSpan().SequenceEqual(p.Second.Raw));
}
