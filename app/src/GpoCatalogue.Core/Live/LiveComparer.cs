using GpoCatalogue.Core.Decode;
using GpoCatalogue.Core.Model;
using GpoCatalogue.Core.Pol;

namespace GpoCatalogue.Core.Live;

/// <summary>How the registry relates to what the policy object says about a policy.</summary>
public enum LiveStatus
{
    /// <summary>Policy not configured and nothing found in the registry.</summary>
    None,

    /// <summary>The registry holds what the policy object prescribes.</summary>
    Applied,

    /// <summary>Configured in the policy object, but the registry differs: not applied yet, or overridden.</summary>
    NotApplied,

    /// <summary>Not configured in the policy object, yet the registry holds policy values. Something else set them.</summary>
    RegistryOnly,

    /// <summary>
    /// Values exist, but in a part of the registry that is not reserved for policy, where Windows and
    /// programs keep their own settings. Says nothing about policy.
    /// </summary>
    OrdinarySetting,
}

public sealed record LiveResult(LiveStatus Status, Decoded? Decoded, IReadOnlyList<string> Lines)
{
    public static readonly LiveResult Nothing = new(LiveStatus.None, null, []);
}

public sealed class LiveComparer(PolicyDecoder decoder, IRegistryReader registry)
{
    private static readonly string[] PolicyRoots =
    [
        @"software\policies\",
        @"software\microsoft\windows\currentversion\policies\",
    ];

    /// <summary>True for keys Windows reserves for policy and clears when a policy is withdrawn.</summary>
    public static bool IsPolicyKey(string key) =>
        PolicyRoots.Any(root => (Slot.NormKey(key) + "\\").StartsWith(root, StringComparison.Ordinal));

    /// <summary>True when un-configuring leaves the policy's values behind in the registry.</summary>
    public static bool LeavesValuesBehind(PolicyDef pol) => OwnedKeys(pol).Any(k => !IsPolicyKey(k));

    public LiveResult Evaluate(PolicyDef pol, Scope scope, IReadOnlyList<PolEntry> configured)
    {
        if (!registry.IsAvailable)
            return LiveResult.Nothing;
        return configured.Count > 0 ? Compare(scope, configured) : Probe(pol, scope);
    }

    private LiveResult Compare(Scope scope, IReadOnlyList<PolEntry> configured)
    {
        var applied = true;
        var lines = new List<string>();
        foreach (var e in configured)
        {
            if (e.Marker is not (PolMarker.None or PolMarker.Del))
                continue;
            var live = registry.ReadValue(scope, e.Key, e.Name);
            lines.Add(live?.RawLine(scope) ?? $"{scope.Hive()}\\{e.Key}!{e.Name} (absent)");
            if (e.Soft)
                continue; // written only when missing, so any existing value is in order
            if (e.Marker == PolMarker.Del ? live is not null : live is null || !SameData(live, e))
                applied = false;
        }
        return new LiveResult(applied ? LiveStatus.Applied : LiveStatus.NotApplied, null, lines);
    }

    private LiveResult Probe(PolicyDef pol, Scope scope)
    {
        var found = new List<PolEntry>();
        foreach (var (key, name) in OwnedValues(pol))
        {
            if (registry.ReadValue(scope, key, name) is { } value && !found.Contains(value))
                found.Add(value);
        }
        foreach (var el in pol.Elements.Where(e => e.IsList))
        {
            // Values another policy names outright are not part of the list.
            found.AddRange(registry.ReadKey(scope, el.Key)
                .Where(v => !decoder.Index.HasPrimaryOwner(scope, v.Key, v.ValueName) && !found.Contains(v)));
        }
        found = found.Where(v => decoder.Index.Owns(scope, v, pol.Uid)).ToList();
        if (found.Count == 0)
            return LiveResult.Nothing;

        var status = found.All(v => IsPolicyKey(v.Key)) ? LiveStatus.RegistryOnly : LiveStatus.OrdinarySetting;
        return new LiveResult(status, decoder.Decode(pol, scope, found), found.Select(v => v.RawLine(scope)).ToList());
    }

    private static IEnumerable<(string Key, string Name)> OwnedValues(PolicyDef pol)
    {
        if (!string.IsNullOrEmpty(pol.ValueName))
            yield return (pol.RegistryKey, pol.ValueName);
        foreach (var it in pol.EnabledList.Concat(pol.DisabledList))
            yield return (it.Key, it.Name);
        foreach (var el in pol.Elements.Where(e => !e.IsList))
        {
            if (!string.IsNullOrEmpty(el.ValueName))
                yield return (el.Key, el.ValueName);
            foreach (var it in el.Items.SelectMany(i => i.ValueList).Concat(el.TrueList).Concat(el.FalseList))
                yield return (it.Key, it.Name);
        }
    }

    private static IEnumerable<string> OwnedKeys(PolicyDef pol) =>
        OwnedValues(pol).Select(v => v.Key).Concat(pol.Elements.Where(e => e.IsList).Select(e => e.Key));

    private static bool SameData(PolEntry live, PolEntry expected)
    {
        if (live.Type != expected.Type)
            return false;
        return (live.Data, expected.Data) switch
        {
            (null, null) => true,
            (string a, string b) => a == b,
            (string[] a, string[] b) => a.SequenceEqual(b),
            (long a, long b) => a == b,
            (ulong a, ulong b) => a == b,
            _ => live.Raw.AsSpan().SequenceEqual(expected.Raw),
        };
    }
}
