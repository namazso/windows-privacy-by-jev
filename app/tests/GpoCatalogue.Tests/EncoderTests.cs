using GpoCatalogue.Core.Decode;
using GpoCatalogue.Core.Encode;
using GpoCatalogue.Core.Model;
using GpoCatalogue.Core.Pol;

namespace GpoCatalogue.Tests;

/// <summary>
/// Microsoft authored the baselines with the Group Policy editor, so they show what the editor writes.
/// Decoding a baseline application and encoding the result again must give back the same entries.
/// </summary>
public class EncoderTests
{
    private static readonly PolicyDecoder Decoder = new(Repo.Catalog.Index);

    /// <summary>Baseline applications that were not written the way the editor writes them.</summary>
    private static readonly Dictionary<string, string> KnownExceptions = new()
    {
        // The Security Baseline sets only the per-process values and leaves the policy's own value out.
        ["sb|inetres:IESF_PolicyProcessList_3"] = "main value missing in the baseline",
        ["sb|inetres:IESF_PolicyProcessList_5"] = "main value missing in the baseline",
        ["sb|inetres:IESF_PolicyProcessList_6"] = "main value missing in the baseline",
        ["sb|inetres:IESF_PolicyProcessList_8"] = "main value missing in the baseline",
        ["sb|inetres:IESF_PolicyProcessList_9"] = "main value missing in the baseline",
        ["sb|inetres:IESF_PolicyProcessList_10"] = "main value missing in the baseline",
        ["sb|inetres:IESF_PolicyProcessList_11"] = "main value missing in the baseline",
        ["sb|inetres:IESF_PolicyProcessList_12"] = "main value missing in the baseline",
        // RTLFB disables SmartScreen without removing the level the option would have set.
        ["rtlfb|SmartScreen:ShellConfigureSmartScreen"] = "baseline omits **del.ShellSmartScreenLevel",
        ["rtlfb|WindowsExplorer:EnableSmartScreen"] = "baseline omits **del.ShellSmartScreenLevel",
    };

    private static PolicyConfig ConfigOf(PolicyDef pol, Scope scope, IReadOnlyList<PolEntry> entries)
    {
        var pairs = Decoder.Pair(pol, scope, entries);
        var state = PolicyDecoder.DeriveState(pol, pairs, scope).State;
        return new PolicyConfig(state, state == PolicyState.Enabled ? OptionReader.Read(pol, pairs) : []);
    }

    [Fact]
    public void Encoding_a_decoded_baseline_gives_back_its_entries()
    {
        var differences = new Dictionary<string, string>();
        var total = 0;
        foreach (var (pol, baseline, app) in DecoderParityTests.Applications())
        {
            total++;
            var entries = app.Entries.Select(PolEntry.FromRaw).ToList();
            var encoded = PolicyEncoder.Encode(pol, ConfigOf(pol, app.ScopeValue, entries));
            if (!SectionState.Equivalent(entries, encoded))
            {
                differences[$"{baseline}|{pol.Uid}"] =
                    $"\n  baseline: {string.Join(" ; ", entries)}\n  encoded:  {string.Join(" ; ", encoded)}";
            }
        }

        var unexpected = differences.Where(d => !KnownExceptions.ContainsKey(d.Key)).ToList();
        var stale = KnownExceptions.Keys.Where(k => !differences.ContainsKey(k)).ToList();

        Assert.Equal(375, total);
        Assert.True(unexpected.Count == 0, $"{unexpected.Count} unexpected differences:\n"
            + string.Join("\n", unexpected.Take(40).Select(d => d.Key + d.Value)));
        Assert.True(stale.Count == 0, "Listed as exceptions but they match: " + string.Join(", ", stale));
    }

    [Fact]
    public void Encoding_is_stable_under_decoding()
    {
        var failures = new List<string>();
        foreach (var (pol, baseline, app) in DecoderParityTests.Applications())
        {
            var entries = app.Entries.Select(PolEntry.FromRaw).ToList();
            var once = PolicyEncoder.Encode(pol, ConfigOf(pol, app.ScopeValue, entries));
            if (once.Count == 0)
                continue;
            var twice = PolicyEncoder.Encode(pol, ConfigOf(pol, app.ScopeValue, once));
            if (!SectionState.Equivalent(once, twice))
                failures.Add($"{baseline}|{pol.Uid}");
        }

        Assert.True(failures.Count == 0, "Not stable: " + string.Join(", ", failures.Take(20)));
    }

    [Fact]
    public void Implicit_values_are_one_and_delete()
    {
        var pol = Repo.Catalog["Conf:DisableChat"];
        Assert.Null(pol.EnabledValue);

        var enabled = Assert.Single(PolicyEncoder.Encode(pol, PolicyConfig.Enabled()));
        var disabled = Assert.Single(PolicyEncoder.Encode(pol, PolicyConfig.Disabled));

        Assert.Equal((pol.ValueName, RegType.Dword, (object)1L), (enabled.ValueName, enabled.Type, enabled.Data));
        Assert.Equal((PolMarker.Del, pol.ValueName), (disabled.Marker, disabled.Name));
        Assert.Equal(PolEntry.MarkerData, disabled.Raw);
        Assert.Empty(PolicyEncoder.Encode(pol, PolicyConfig.NotConfigured));
    }

    [Fact]
    public void Enum_writes_the_chosen_value()
    {
        var pol = Repo.Catalog["DataCollection:AllowTelemetry"];
        var config = new PolicyConfig(PolicyState.Enabled, new Dictionary<string, OptionValue>
        {
            ["AllowTelemetry"] = new EnumOption(2),
        });

        var entry = Assert.Single(PolicyEncoder.Encode(pol, config));

        Assert.Equal(("AllowTelemetry", (object)3L), (entry.ValueName, entry.Data));
        Assert.Equal("Enabled; Allow Diagnostic Data = Send optional diagnostic data",
            Decoder.Decode(pol, Scope.Machine, [entry])!.Summary);
    }

    [Fact]
    public void Non_additive_list_clears_the_key_first_and_numbers_its_values()
    {
        var pol = Repo.Catalog["DeviceInstallation:DeviceInstall_Classes_Deny"];
        var list = pol.Elements.Single(e => e.IsList);
        Assert.False(list.Additive);
        Assert.Equal("", list.ValuePrefix);
        var options = OptionReader.Defaults(pol);
        options[list.Id] = new ListOption([new("", "{a}"), new("", "{b}")]);

        var entries = PolicyEncoder.Encode(pol, new PolicyConfig(PolicyState.Enabled, options));
        var inListKey = entries.Where(e => PolicyDecoder.SameKey(e.Key, list.Key)).ToList();

        Assert.Equal(PolMarker.DelVals, inListKey[0].Marker);
        Assert.Equal(["1", "2"], inListKey.Skip(1).Select(e => e.ValueName));
        Assert.Equal(["{a}", "{b}"], inListKey.Skip(1).Select(e => e.Data));
    }

    [Fact]
    public void Disabling_removes_element_values()
    {
        var pol = Repo.Catalog["RemoteAssistance:RA_Solicit"];

        var entries = PolicyEncoder.Encode(pol, PolicyConfig.Disabled);

        Assert.Equal(pol.Elements.Count + 1, entries.Count);
        Assert.All(entries.Skip(1), e => Assert.Equal(PolMarker.Del, e.Marker));
        Assert.Equal(PolicyState.Disabled, Decoder.Decode(pol, Scope.Machine, entries)!.State);
    }

    [Fact]
    public void Text_honours_expandable_and_decimal_honours_store_as_text()
    {
        var expandable = Repo.Catalog["EventViewer:EventViewer_RedirectionProgram"];
        var el = expandable.Elements.Single();
        Assert.True(el.Expandable);
        var entries = PolicyEncoder.Encode(expandable, new PolicyConfig(PolicyState.Enabled,
            new Dictionary<string, OptionValue> { [el.Id] = new TextOption(@"%SystemRoot%\x.exe") }));
        Assert.Contains(entries, e => e.Type == RegType.ExpandSz && (string?)e.Data == @"%SystemRoot%\x.exe");

        var asText = Repo.Catalog["MSS-legacy:Pol_MSS_ScreenSaverGracePeriod"];
        var number = asText.Elements.Single();
        Assert.True(number.StoreAsText);
        var written = PolicyEncoder.Encode(asText, new PolicyConfig(PolicyState.Enabled,
            new Dictionary<string, OptionValue> { [number.Id] = new NumberOption(5) }));
        Assert.Contains(written, e => e.Type == RegType.Sz && (string?)e.Data == "5");
    }

    [Fact]
    public void Validation_reports_missing_and_out_of_range_values()
    {
        var pol = Repo.Catalog["Conf:SetAVThroughput"];
        var el = pol.Elements.Single();

        var tooLow = PolicyEncoder.Validate(pol, new PolicyConfig(PolicyState.Enabled,
            new Dictionary<string, OptionValue> { [el.Id] = new NumberOption(el.Min - 1) }));
        var fine = PolicyEncoder.Validate(pol, new PolicyConfig(PolicyState.Enabled,
            new Dictionary<string, OptionValue> { [el.Id] = new NumberOption(el.Min) }));

        Assert.Single(tooLow);
        Assert.Empty(fine);
        Assert.Empty(PolicyEncoder.Validate(pol, PolicyConfig.Disabled));
    }

    [Fact]
    public void Defaults_come_from_the_template()
    {
        var telemetry = Repo.Catalog["DataCollection:AllowTelemetry"];
        Assert.Equal(new EnumOption(1), OptionReader.Defaults(telemetry)["AllowTelemetry"]);

        // A number box falls back to 1, moved into the permitted range.
        var throughput = Repo.Catalog["Conf:SetAVThroughput"];
        var el = throughput.Elements.Single();
        var value = Assert.IsType<NumberOption>(OptionReader.Defaults(throughput)[el.Id]);
        Assert.InRange(value.Value, el.Min, el.Max);
    }
}
