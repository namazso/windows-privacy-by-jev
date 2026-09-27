using GpoCatalogue.Core.Model;
using GpoCatalogue.Core.Pol;

namespace GpoCatalogue.Core.Decode;

/// <summary>What a set of policy-file entries configures a policy to.</summary>
public sealed record Decoded(
    Scope Scope,
    PolicyState State,
    string Summary,
    IReadOnlyList<KeyValuePair<string, string>> Values,
    IReadOnlyList<string> Raw,
    IReadOnlyList<string> Flags)
{
    public bool Has(string flag) => Flags.Contains(flag);
}

public static class Flag
{
    public const string SharedValue = "shared_value", MainValueMissing = "main_value_missing",
        NonstandardDisable = "nonstandard_disable", UnrecognizedValue = "unrecognized_value",
        PartialList = "partial_list", Soft = "soft";
}

/// <summary>
/// Derives the configured state of policies from policy-file entries. A line-for-line port of
/// <c>derive_state</c> and its helpers in pipeline/regindex.py; the published dataset is the reference.
/// </summary>
public sealed class PolicyDecoder(RegIndex index)
{
    private static readonly ValueSpec ImplicitEnabled = ValueSpec.Dword(1);
    private static readonly ValueSpec ImplicitDisabled = ValueSpec.Deleted();

    public RegIndex Index { get; } = index;

    /// <summary>Pairs each entry with the roles through which it belongs to <paramref name="pol"/>.</summary>
    public List<(PolEntry Entry, Hit Hit)> Pair(PolicyDef pol, Scope scope, IEnumerable<PolEntry> entries) =>
        entries.SelectMany(e => Index.Resolve(scope, e, pol.Uid).Select(h => (e, h))).ToList();

    /// <summary>Decodes the entries that belong to one policy; null when none do (Not configured).</summary>
    public Decoded? Decode(PolicyDef pol, Scope scope, IEnumerable<PolEntry> entries)
    {
        var pairs = Pair(pol, scope, entries);
        if (pairs.Count == 0)
            return null;
        var decoded = DeriveState(pol, pairs, scope);
        // Flag only values that two policies own outright; catch-all list sharing is routine.
        var shared = pairs.Select(p => Index.Resolve(scope, p.Entry))
            .Any(hits => hits.Select(h => h.Uid).Distinct().Count() > 1 && hits.All(h => h.IsPrimary));
        var flags = decoded.Flags.ToList();
        if (shared && !flags.Contains(Flag.SharedValue))
            flags.Add(Flag.SharedValue);
        if (pairs.Any(p => p.Entry.Soft) && !flags.Contains(Flag.Soft))
            flags.Add(Flag.Soft);
        return decoded with { Flags = flags };
    }

    public static bool SpecMatches(ValueSpec? spec, PolEntry entry)
    {
        if (spec is null)
            return false;
        if (spec.IsDelete)
            return entry.Marker == PolMarker.Del;
        if (entry.IsMarker)
            return false;
        var data = entry.Data;
        if (spec.IsNumber)
        {
            return data switch
            {
                long n => n == spec.Num,
                // storeAsText decimals arrive as REG_SZ
                string s => PyFormat.TryParseInt(s, out var parsed) && parsed == spec.Num,
                _ => false,
            };
        }
        if (spec.Kind == ValueSpec.String)
        {
            return data is string text
                && string.Equals(text.Trim(), (spec.Str ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    public static bool SameValue(string a, string? b) =>
        string.Equals(a, b ?? "", StringComparison.OrdinalIgnoreCase);

    public static bool SameKey(string a, string b) => Slot.NormKey(a) == Slot.NormKey(b);

    /// <summary>
    /// Some policies name the same registry value as their only element; the value then carries the option
    /// rather than an on/off flag.
    /// </summary>
    public static bool IsSelfValued(PolicyDef pol) =>
        pol.EnabledValue is null && pol.DisabledValue is null && pol.Elements.Any(el =>
            !string.IsNullOrEmpty(el.ValueName) && SameValue(el.ValueName, pol.ValueName)
            && SameKey(el.Key, pol.RegistryKey));

    public static Decoded DeriveState(PolicyDef pol, IReadOnlyList<(PolEntry Entry, Hit Hit)> pairs, Scope scope)
    {
        var flags = new List<string>();
        List<PolEntry> Entries(HitRole role) => pairs.Where(p => p.Hit.Role == role).Select(p => p.Entry).ToList();

        PolicyState? state = null;
        var main = Entries(HitRole.Main);
        var selfValued = IsSelfValued(pol);
        if (!string.IsNullOrEmpty(pol.ValueName))
        {
            if (main.Count > 0)
            {
                var e = main[0];
                var enabled = pol.EnabledValue ?? ImplicitEnabled;
                var disabled = pol.DisabledValue ?? ImplicitDisabled;
                if (selfValued)
                {
                    // Any concrete value means Enabled.
                    state = e.Marker == PolMarker.Del ? PolicyState.Disabled : PolicyState.Enabled;
                }
                else if (SpecMatches(enabled, e))
                {
                    state = PolicyState.Enabled;
                }
                else if (SpecMatches(disabled, e))
                {
                    state = PolicyState.Disabled;
                }
                else if (e.Marker == PolMarker.Del || (pol.DisabledValue is null && e.Data is long n && n == 0))
                {
                    state = PolicyState.Disabled;
                    flags.Add(Flag.NonstandardDisable);
                }
                else
                {
                    state = PolicyState.Enabled;
                    flags.Add(Flag.UnrecognizedValue);
                }
            }
            else
            {
                flags.Add(Flag.MainValueMissing);
            }
        }
        if (state is null && (pol.EnabledList.Count > 0 || pol.DisabledList.Count > 0))
        {
            var en = Entries(HitRole.EnabledList);
            var dis = Entries(HitRole.DisabledList);
            if (AllPresent(pol.EnabledList, en))
            {
                state = PolicyState.Enabled;
            }
            else if (AllPresent(pol.DisabledList, dis))
            {
                state = PolicyState.Disabled;
            }
            else if (en.Count > 0 || dis.Count > 0)
            {
                state = en.Count > 0 ? PolicyState.Enabled : PolicyState.Disabled;
                flags.Add(Flag.PartialList);
            }
        }
        if (state is null)
        {
            var elementEntries = pairs
                .Where(p => p.Hit.Role is HitRole.Element or HitRole.List or HitRole.EnumValueList or HitRole.BoolList)
                .Select(p => p.Entry).ToList();
            if (elementEntries.Count > 0 && elementEntries.All(e => e.IsMarker))
            {
                state = PolicyState.Disabled;
            }
            else if (elementEntries.Count > 0)
            {
                state = PolicyState.Enabled;
            }
            else
            {
                state = PolicyState.Enabled;
                flags.Add(Flag.UnrecognizedValue);
            }
        }

        var values = new List<KeyValuePair<string, string>>();
        if (state == PolicyState.Enabled)
        {
            foreach (var el in pol.Elements)
            {
                var ents = ElementEntries(el, pairs);
                if (ents.Count > 0)
                    values.Add(new(el.Label, RenderElement(el, ents)));
            }
        }

        var raw = new List<string>();
        foreach (var (e, _) in pairs)
        {
            var line = e.RawLine(scope);
            if (!raw.Contains(line))
                raw.Add(line);
        }

        var summary = state.Value.ToString();
        if (values.Count > 0)
            summary += "; " + string.Join("; ", values.Select(v => $"{v.Key} = {v.Value}"));
        return new Decoded(scope, state.Value, summary, values, raw, flags);
    }

    /// <summary>The entries that carry the value of one element.</summary>
    public static List<PolEntry> ElementEntries(ElementDef el, IEnumerable<(PolEntry Entry, Hit Hit)> pairs) =>
        pairs.Where(p => p.Hit.ElemId == el.Id && p.Hit.Role is HitRole.Element or HitRole.List)
            .Select(p => p.Entry).ToList();

    /// <summary>The entry an element reads its value from.</summary>
    public static PolEntry ValueEntry(ElementDef el, IReadOnlyList<PolEntry> entries) =>
        entries.FirstOrDefault(x => SameValue(x.Name, el.ValueName)) ?? entries[0];

    private static bool AllPresent(List<ListItemDef> items, List<PolEntry> entries)
    {
        if (items.Count == 0)
            return false;
        return items.All(it => entries.Any(e =>
            SameKey(e.Key, it.Key) && SameValue(e.Name, it.Name) && SpecMatches(it.Spec, e)));
    }

    /// <summary>List items in dialog order: numbered names by number, the rest alphabetically.</summary>
    public static List<PolEntry> SortListItems(ElementDef el, IEnumerable<PolEntry> entries)
    {
        (int Rank, long Number, string Text) SortKey(PolEntry e)
        {
            var name = e.Name;
            if (!string.IsNullOrEmpty(el.ValuePrefix)
                && name.StartsWith(el.ValuePrefix, StringComparison.OrdinalIgnoreCase))
                name = name[el.ValuePrefix.Length..];
            return name.Length > 0 && name.All(char.IsAsciiDigit) && long.TryParse(name, out var n)
                ? (0, n, "")
                : (1, 0, name.ToLowerInvariant());
        }

        // OrderBy is stable, as Python's sort is.
        return entries.Where(e => !e.IsMarker)
            .OrderBy(e => SortKey(e).Rank)
            .ThenBy(e => SortKey(e).Number)
            .ThenBy(e => SortKey(e).Text, StringComparer.Ordinal)
            .ToList();
    }

    private static string RenderList(ElementDef el, List<PolEntry> entries)
    {
        var prefix = entries.Any(e => e.Marker == PolMarker.DelVals) ? "(replace existing) " : "";
        var rendered = SortListItems(el, entries)
            .Select(e => el.ExplicitValue ? $"{e.Name} = {PyFormat.Str(e.Data)}" : PyFormat.Str(e.Data))
            .ToList();
        return prefix + (rendered.Count > 0 ? "[" + string.Join("; ", rendered) + "]" : "(empty)");
    }

    private static string RenderElement(ElementDef el, List<PolEntry> entries)
    {
        if (el.IsList)
            return RenderList(el, entries);
        var e = ValueEntry(el, entries);
        switch (el.Type)
        {
            case ElementDef.Enum:
                foreach (var item in el.Items)
                {
                    if (SpecMatches(item.Spec, e))
                        return item.Display;
                }
                return e.IsMarker ? "(removed)" : e.Render();
            case ElementDef.Boolean:
                if (SpecMatches(el.TrueSpec, e))
                    return "Yes";
                if (SpecMatches(el.FalseSpec, e) || e.Marker == PolMarker.Del)
                    return "No";
                return e.Render();
        }
        if (e.IsMarker)
            return "(removed)";
        switch (el.Type)
        {
            case ElementDef.MultiText:
                return e.Data is string[] lines ? string.Join(" | ", lines) : PyFormat.Str(e.Data);
            case ElementDef.Text:
                var text = e.Data is null ? "" : PyFormat.Str(e.Data);
                return text.Trim().Length == 0 ? PyFormat.Repr(text) : text;
            default:
                return PyFormat.Str(e.Data);
        }
    }
}
