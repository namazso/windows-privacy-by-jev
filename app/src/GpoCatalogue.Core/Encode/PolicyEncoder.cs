using System.Globalization;
using GpoCatalogue.Core.Model;
using GpoCatalogue.Core.Pol;

namespace GpoCatalogue.Core.Encode;

/// <summary>
/// Turns a state and option values into the policy-file entries the Group Policy editor writes for them.
/// The rules reproduce the entries found in Microsoft's baselines.
/// </summary>
public static class PolicyEncoder
{
    public static List<PolEntry> Encode(PolicyDef pol, PolicyConfig config)
    {
        var entries = new List<PolEntry>();
        switch (config.State)
        {
            case PolicyState.Enabled:
                if (!string.IsNullOrEmpty(pol.ValueName))
                    entries.Add(FromSpec(pol.RegistryKey, pol.ValueName, pol.EnabledValue ?? ValueSpec.Dword(1)));
                entries.AddRange(pol.EnabledList.Select(FromItem));
                foreach (var el in pol.Elements)
                    EncodeEnabled(el, config.Options.GetValueOrDefault(el.Id), entries);
                break;
            case PolicyState.Disabled:
                if (!string.IsNullOrEmpty(pol.ValueName))
                    entries.Add(FromSpec(pol.RegistryKey, pol.ValueName, pol.DisabledValue ?? ValueSpec.Deleted()));
                entries.AddRange(pol.DisabledList.Select(FromItem));
                foreach (var el in pol.Elements)
                    EncodeDisabled(el, entries);
                break;
        }
        // Elements are written after the policy value, so an element that owns the policy's value wins.
        return SectionState.Merge([], entries);
    }

    /// <summary>What stops <paramref name="config"/> from being written, one message per problem.</summary>
    public static List<string> Validate(PolicyDef pol, PolicyConfig config)
    {
        var problems = new List<string>();
        if (config.State != PolicyState.Enabled)
            return problems;
        foreach (var el in pol.Elements)
        {
            var value = config.Options.GetValueOrDefault(el.Id);
            switch (el.Type)
            {
                case ElementDef.Enum:
                    if (el.Required && value is not EnumOption { Index: >= 0 })
                        problems.Add($"{el.Label}: choose a value.");
                    break;
                case ElementDef.Decimal or ElementDef.LongDecimal:
                    if (value is NumberOption n)
                    {
                        if (n.Value < el.Min || n.Value > el.Max)
                            problems.Add($"{el.Label}: enter a number from {el.Min} to {el.Max}.");
                    }
                    else if (el.Required)
                    {
                        problems.Add($"{el.Label}: enter a number.");
                    }
                    break;
                case ElementDef.Text:
                    var text = (value as TextOption)?.Text ?? "";
                    if (el.Required && text.Length == 0)
                        problems.Add($"{el.Label}: enter a value.");
                    if (text.Length > el.TextLimit)
                        problems.Add($"{el.Label}: at most {el.TextLimit} characters.");
                    break;
                case ElementDef.MultiText:
                    var lines = (value as MultiTextOption)?.Lines.Where(l => l.Length > 0).ToList() ?? [];
                    if (el.Required && lines.Count == 0)
                        problems.Add($"{el.Label}: enter at least one line.");
                    if (el.MaxStrings is { } max && max > 0 && lines.Count > max)
                        problems.Add($"{el.Label}: at most {max} lines.");
                    if (lines.Any(l => l.Length > el.TextLimit))
                        problems.Add($"{el.Label}: at most {el.TextLimit} characters per line.");
                    break;
                case ElementDef.List:
                    var items = (value as ListOption)?.Items ?? [];
                    if (el.Required && items.Count == 0)
                        problems.Add($"{el.Label}: add at least one entry.");
                    if (items.Any(i => i.Value.Length == 0))
                        problems.Add($"{el.Label}: entries cannot be empty.");
                    if (el.ExplicitValue)
                    {
                        if (items.Any(i => i.Key.Length == 0))
                            problems.Add($"{el.Label}: every entry needs a name.");
                        if (items.Select(i => i.Key.ToLowerInvariant()).Distinct().Count() != items.Count)
                            problems.Add($"{el.Label}: names must be unique.");
                    }
                    else if (el.ValuePrefix is null
                             && items.Select(i => i.Value.ToLowerInvariant()).Distinct().Count() != items.Count)
                    {
                        problems.Add($"{el.Label}: entries must be unique.");
                    }
                    break;
            }
        }
        return problems;
    }

    public static PolEntry FromSpec(string key, string name, ValueSpec spec) => spec.Kind switch
    {
        ValueSpec.Delete => PolEntry.Delete(key, name),
        ValueSpec.String => PolEntry.Text(key, name, spec.Str ?? ""),
        ValueSpec.LongDecimal => PolEntry.Qword(key, name, unchecked((ulong)(spec.Num ?? 0))),
        _ => PolEntry.Dword(key, name, unchecked((uint)(spec.Num ?? 0))),
    };

    private static PolEntry FromItem(ListItemDef item) => FromSpec(item.Key, item.Name, item.Spec);

    private static void EncodeEnabled(ElementDef el, OptionValue? value, List<PolEntry> entries)
    {
        var name = el.ValueName ?? "";
        switch (el.Type)
        {
            case ElementDef.Enum:
                if (value is EnumOption { Index: >= 0 } choice && choice.Index < el.Items.Count)
                {
                    var item = el.Items[choice.Index];
                    if (item.Spec is not null)
                        entries.Add(FromSpec(el.Key, name, item.Spec));
                    entries.AddRange(item.ValueList.Select(FromItem));
                }
                break;
            case ElementDef.Boolean:
                // The dialog always supplies a checkbox state; an absent one leaves the value alone.
                if (value is BoolOption box)
                {
                    entries.Add(FromSpec(el.Key, name, box.Checked
                        ? el.TrueSpec ?? ValueSpec.Dword(1)
                        : el.FalseSpec ?? ValueSpec.Deleted()));
                    entries.AddRange((box.Checked ? el.TrueList : el.FalseList).Select(FromItem));
                }
                break;
            case ElementDef.Decimal or ElementDef.LongDecimal:
                if (value is NumberOption number)
                {
                    if (el.StoreAsText)
                        entries.Add(PolEntry.Text(el.Key, name, number.Value.ToString(CultureInfo.InvariantCulture)));
                    else if (el.Type == ElementDef.LongDecimal)
                        entries.Add(PolEntry.Qword(el.Key, name, unchecked((ulong)number.Value)));
                    else
                        entries.Add(PolEntry.Dword(el.Key, name, unchecked((uint)number.Value)));
                }
                break;
            case ElementDef.Text:
                if (value is TextOption { Text.Length: > 0 } text)
                    entries.Add(PolEntry.Text(el.Key, name, text.Text, el.Expandable));
                break;
            case ElementDef.MultiText:
                var lines = (value as MultiTextOption)?.Lines.Where(l => l.Length > 0).ToList() ?? [];
                if (lines.Count > 0)
                    entries.Add(PolEntry.MultiText(el.Key, name, lines));
                break;
            case ElementDef.List:
                if (!el.Additive)
                    entries.Add(PolEntry.DeleteValues(el.Key));
                var items = (value as ListOption)?.Items ?? [];
                for (var i = 0; i < items.Count; i++)
                {
                    var itemName = el.ExplicitValue ? items[i].Key
                        : el.ValuePrefix is not null
                            ? el.ValuePrefix + (i + 1).ToString(CultureInfo.InvariantCulture)
                            : items[i].Value;
                    entries.Add(PolEntry.Text(el.Key, itemName, items[i].Value, el.Expandable));
                }
                break;
        }
    }

    private static void EncodeDisabled(ElementDef el, List<PolEntry> entries)
    {
        if (el.IsList)
        {
            entries.Add(PolEntry.DeleteValues(el.Key));
            return;
        }
        var name = el.ValueName ?? "";
        if (el.Type == ElementDef.Boolean && el.FalseSpec is { IsDelete: false } off)
        {
            // A checkbox with a real "off" value is switched off rather than removed.
            entries.Add(FromSpec(el.Key, name, off));
            entries.AddRange(el.FalseList.Select(FromItem));
            return;
        }
        entries.Add(PolEntry.Delete(el.Key, name));
    }
}
