using System.Text.Json;
using GpoCatalogue.Core.Decode;
using GpoCatalogue.Core.Model;
using GpoCatalogue.Core.Pol;

namespace GpoCatalogue.Tests;

/// <summary>
/// The decoder is a port of pipeline/regindex.py. These tests hold it to the states the Python pipeline
/// published in data/policies.json for every policy the two baselines configure.
/// </summary>
public class DecoderParityTests
{
    private static readonly PolicyDecoder Decoder = new(Repo.Catalog.Index);

    public static IEnumerable<(PolicyDef Policy, string Baseline, BaselineApplication Application)> Applications() =>
        Repo.Catalog.Policies.SelectMany(p =>
            p.Rtlfb.Select(a => (p, "rtlfb", a)).Concat(p.Sb.Select(a => (p, "sb", a))));

    [Fact]
    public void Catalogue_has_every_policy_and_baseline_application()
    {
        Assert.Equal(3626, Repo.Catalog.Policies.Count);
        Assert.Equal(375, Applications().Count());
        Assert.Equal(109, Repo.Catalog.Policies.Count(p => p.Rtlfb.Count > 0));
        Assert.Equal(266, Repo.Catalog.Policies.Count(p => p.Sb.Count > 0));
    }

    [Fact]
    public void Every_baseline_application_decodes_as_published()
    {
        var site = Repo.SiteData.GetProperty("policies").EnumerateArray()
            .ToDictionary(p => p.GetProperty("uid").GetString()!);
        var failures = new List<string>();

        foreach (var (pol, baseline, app) in Applications())
        {
            var published = site[pol.Uid].GetProperty(baseline).EnumerateArray()
                .Single(a => a.GetProperty("gpo").GetString() == app.Gpo
                             && a.GetProperty("scope").GetString() == app.Scope);
            var decoded = Decoder.Decode(pol, app.ScopeValue, app.Entries.Select(PolEntry.FromRaw));
            if (decoded is null)
            {
                failures.Add($"{pol.Uid} [{baseline} {app.Gpo}]: no entries mapped");
                continue;
            }

            void Check(string what, string expected, string actual)
            {
                if (expected != actual)
                    failures.Add($"{pol.Uid} [{baseline} {app.Gpo}] {what}:\n  expected {expected}\n  actual   {actual}");
            }

            Check("state", published.GetProperty("state").GetString()!, decoded.State.ToString());
            Check("summary", published.GetProperty("summary").GetString()!, decoded.Summary);
            Check("values", Join(published.GetProperty("values").EnumerateArray()
                    .Select(v => $"{v[0].GetString()}={v[1].GetString()}")),
                Join(decoded.Values.Select(v => $"{v.Key}={v.Value}")));
            Check("raw", Join(Strings(published.GetProperty("raw"))), Join(decoded.Raw));
            Check("flags", Join(Strings(published.GetProperty("flags"))), Join(decoded.Flags));
        }

        Assert.True(failures.Count == 0, $"{failures.Count} differences:\n" + string.Join("\n", failures.Take(25)));
    }

    // pipeline/verify.py SPOT_CHECKS
    [Theory]
    [InlineData("DataCollection:AllowTelemetry", "rtlfb", "Enabled; Allow Diagnostic Data = Diagnostic data off (not recommended)")]
    [InlineData("CloudContent:DisableWindowsConsumerFeatures", "rtlfb", "Enabled")]
    [InlineData("WindowsUpdate:CorpWuURL", "rtlfb", "Set the intranet statistics server = \" \"")]
    [InlineData("WindowsDefender:SignatureUpdate_DefinitionUpdateFileSharesSources", "rtlfb", "Disabled")]
    [InlineData("WindowsDefender:SpynetReporting", "rtlfb", "Enabled; Join Microsoft MAPS = Disabled")]
    [InlineData("SecGuide:Pol_SecGuide_0002_SMBv1_ClientDriver", "sb", "Enabled; Configure MrxSmb10 driver = Disable driver (recommended)")]
    [InlineData("PowerShellExecutionPolicy:EnableScriptBlockLogging", "sb", "Log script block invocation start / stop events = No")]
    [InlineData("DeviceInstallation:DeviceInstall_Classes_Deny", "sb", "(replace existing) [{d48179be-ec20-11d1-b6b8-00c04fa372a7}]")]
    [InlineData("WindowsExplorer:EnableSmartScreen", "sb", "Warn and prevent bypass")]
    [InlineData("SmartScreen:ShellConfigureSmartScreen", "sb", "Warn and prevent bypass")]
    [InlineData("WindowsDefender:RealtimeProtection_RealtimeScanDirection", "sb", "bi-directional (full on-access)")]
    public void Spot_checks(string uid, string baseline, string expected)
    {
        var pol = Repo.Catalog[uid];
        var summaries = (baseline == "rtlfb" ? pol.Rtlfb : pol.Sb)
            .Select(a => Decoder.Decode(pol, a.ScopeValue, a.Entries.Select(PolEntry.FromRaw))?.Summary ?? "")
            .ToList();

        Assert.Contains(summaries, s => s.Contains(expected));
    }

    [Fact]
    public void A_policy_nothing_maps_to_is_not_configured()
    {
        var pol = Repo.Catalog["Conf:DisableChat"];
        PolEntry[] unrelated = [PolEntry.Dword(@"Software\Policies\Microsoft\Windows Defender", "DisableAntiSpyware", 1)];

        Assert.Null(Decoder.Decode(pol, Scope.User, unrelated));
        Assert.Null(Decoder.Decode(pol, Scope.User, []));
    }

    private static IEnumerable<string> Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()!);

    private static string Join(IEnumerable<string> parts) => string.Join(" || ", parts);
}
