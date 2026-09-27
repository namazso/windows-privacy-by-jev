using GpoCatalogue.Core.Decode;
using GpoCatalogue.Core.Model;
using GpoCatalogue.Core.Pol;

namespace GpoCatalogue.Core.Encode;

/// <summary>The value chosen for one option of a policy, typed by the kind of element.</summary>
public abstract record OptionValue;

/// <summary>Index into the element's items in template order; -1 when nothing is selected.</summary>
public sealed record EnumOption(int Index) : OptionValue;

public sealed record BoolOption(bool Checked) : OptionValue;

public sealed record NumberOption(long Value) : OptionValue;

public sealed record TextOption(string Text) : OptionValue;

public sealed record MultiTextOption(IReadOnlyList<string> Lines) : OptionValue;

/// <summary>Key is the value name and only matters for lists with explicit names.</summary>
public sealed record ListOption(IReadOnlyList<KeyValuePair<string, string>> Items) : OptionValue;

/// <summary>A state and the option values that go with it.</summary>
public sealed record PolicyConfig(PolicyState State, IReadOnlyDictionary<string, OptionValue> Options)
{
    private static readonly IReadOnlyDictionary<string, OptionValue> NoOptions = new Dictionary<string, OptionValue>();

    public static readonly PolicyConfig NotConfigured = new(PolicyState.NotConfigured, NoOptions);
    public static readonly PolicyConfig Disabled = new(PolicyState.Disabled, NoOptions);

    public static PolicyConfig Enabled(IReadOnlyDictionary<string, OptionValue>? options = null) =>
        new(PolicyState.Enabled, options ?? NoOptions);
}

public static class OptionReader
{
    /// <summary>The values gpedit pre-fills when a policy is switched to Enabled.</summary>
    public static Dictionary<string, OptionValue> Defaults(PolicyDef pol)
    {
        var options = new Dictionary<string, OptionValue>();
        foreach (var el in pol.Elements)
        {
            var ui = el.Ui;
            switch (el.Type)
            {
                case ElementDef.Enum:
                    if (ui?.DefNum is { } index && index >= 0 && index < el.Items.Count)
                        options[el.Id] = new EnumOption((int)index);
                    break;
                case ElementDef.Boolean:
                    options[el.Id] = new BoolOption(ui?.DefBool ?? false);
                    break;
                case ElementDef.Decimal or ElementDef.LongDecimal:
                    // The presentation schema defaults the box to 1.
                    options[el.Id] = new NumberOption(Math.Clamp(ui?.DefNum ?? 1, el.Min, Math.Max(el.Min, el.Max)));
                    break;
                case ElementDef.Text:
                    if (!string.IsNullOrEmpty(ui?.DefStr))
                        options[el.Id] = new TextOption(ui.DefStr);
                    break;
            }
        }
        return options;
    }

    /// <summary>Reads the option values back out of the entries that configure a policy.</summary>
    public static Dictionary<string, OptionValue> Read(PolicyDef pol, IReadOnlyList<(PolEntry Entry, Hit Hit)> pairs)
    {
        var options = new Dictionary<string, OptionValue>();
        foreach (var el in pol.Elements)
        {
            var entries = PolicyDecoder.ElementEntries(el, pairs);
            if (entries.Count == 0)
                continue;
            if (el.IsList)
            {
                options[el.Id] = new ListOption(PolicyDecoder.SortListItems(el, entries)
                    .Select(e => new KeyValuePair<string, string>(e.Name, PyFormat.Str(e.Data ?? ""))).ToList());
                continue;
            }
            var e = PolicyDecoder.ValueEntry(el, entries);
            switch (el.Type)
            {
                case ElementDef.Enum:
                    var index = el.Items.FindIndex(item => PolicyDecoder.SpecMatches(item.Spec, e));
                    if (index >= 0)
                        options[el.Id] = new EnumOption(index);
                    break;
                case ElementDef.Boolean:
                    options[el.Id] = new BoolOption(PolicyDecoder.SpecMatches(el.TrueSpec, e)
                        || (!PolicyDecoder.SpecMatches(el.FalseSpec, e) && !e.IsMarker));
                    break;
                case ElementDef.Decimal or ElementDef.LongDecimal:
                    if (e.Data is long n)
                        options[el.Id] = new NumberOption(n);
                    else if (e.Data is string s && PyFormat.TryParseInt(s, out var parsed))
                        options[el.Id] = new NumberOption(parsed);
                    break;
                case ElementDef.Text:
                    if (!e.IsMarker)
                        options[el.Id] = new TextOption(e.Data as string ?? "");
                    break;
                case ElementDef.MultiText:
                    if (!e.IsMarker)
                        options[el.Id] = new MultiTextOption(e.Data as string[] ?? []);
                    break;
            }
        }
        return options;
    }
}
