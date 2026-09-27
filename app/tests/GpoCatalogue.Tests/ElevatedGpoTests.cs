using System.Diagnostics;
using System.Text.Json;
using GpoCatalogue.Core;
using GpoCatalogue.Core.Changes;
using GpoCatalogue.Core.Encode;
using GpoCatalogue.Core.Gpo;
using GpoCatalogue.Core.Live;
using GpoCatalogue.Core.Model;
using GpoCatalogue.Core.Pol;
using GpoCatalogue.Core.View;

namespace GpoCatalogue.Tests;

[CollectionDefinition("Local machine", DisableParallelization = true)]
public sealed class LocalMachineCollection;

/// <summary>
/// Changes the local group policy of this computer through the Group Policy API and checks the result by
/// other means, then puts everything back. Runs only with administrator rights. The policies used affect
/// NetMeeting, Windows Media Player, App-V, SNMP and similar components that are unused on a test machine.
/// </summary>
[Collection("Local machine")]
public class ElevatedGpoTests
{
    private static readonly (string Uid, Scope Scope)[] Targets =
    [
        ("Conf:DisableChat", Scope.User),
        ("Conf:SetAVThroughput", Scope.User),
        ("windowsmediadrm:DisableOnline", Scope.Machine),
        ("appv:Steaming_Autoload", Scope.Machine),
        ("appv:Streaming_Package_Source_Root", Scope.Machine),
        ("appv:Virtualization_JITVAllowList", Scope.Machine),
        ("appv:Reporting_Server_Policy", Scope.Machine),
        ("EventViewer:EventViewer_RedirectionProgram", Scope.Machine),
        ("Snmp:SNMP_Communities", Scope.Machine),
        ("EnhancedStorage:ApprovedSilos", Scope.Machine),
        ("ActiveXInstallService:ApprovedActiveXInstallSites", Scope.Machine),
        ("windowsmediaplayer:ConfigureHTTPProxySettings", Scope.User),
        ("windowsmediaplayer:WindowsStreamingMediaProtocols", Scope.User),
        ("WindowsAnytimeUpgrade:Disabled", Scope.User),
    ];

    /// <summary>Values for every option, so each kind of element is written.</summary>
    private static PolicyConfig Filled(PolicyDef pol)
    {
        var options = OptionReader.Defaults(pol);
        foreach (var el in pol.Elements)
        {
            options[el.Id] = el.Type switch
            {
                ElementDef.Enum => new EnumOption(el.Items.Count - 1),
                ElementDef.Boolean => new BoolOption(true),
                ElementDef.Decimal => new NumberOption(el.Max),
                ElementDef.Text => new TextOption(el.Expandable ? @"%SystemRoot%\gpo-test.exe" : "gpo-test"),
                ElementDef.MultiText => new MultiTextOption(["gpo-test-1.exe", "gpo-test-2.exe"]),
                ElementDef.List => new ListOption(el.ExplicitValue
                    ? [new("https://a.example", "2,2,2,2"), new("https://b.example", "0,0,0,0")]
                    : [new("", "gpo-test-a"), new("", "gpo-test-b")]),
                _ => options[el.Id],
            };
        }
        return PolicyConfig.Enabled(options);
    }

    [Fact]
    public void Changes_written_through_the_group_policy_api_are_what_windows_applies()
    {
        if (!ElevationHelper.IsElevated)
            Assert.Skip("Needs administrator rights.");
        var store = new ComGpoStore();
        Assert.DoesNotContain(store.Preflight(), o => o.Blocking);
        var original = store.Read();
        var data = Path.Combine(Path.GetTempPath(), "GpoCatalogueTests", Guid.NewGuid().ToString("N"));
        var registry = new WindowsRegistryReader();
        var ws = new Workspace(Repo.Catalog, store, registry, new AppPaths(data));
        var policies = Targets.Select(t => (Pol: Repo.Catalog[t.Uid], t.Scope)).ToList();
        Assert.All(policies, p => Assert.False(ws.States[p.Pol.Uid, p.Scope].IsConfigured, p.Pol.Uid));

        try
        {
            // Enabled, with a value in every option.
            foreach (var (pol, scope) in policies)
                ws.Stage(ws.Builder.FromConfig(pol, scope, Filled(pol)));
            Assert.Equal(Targets.Length, ws.Pending.Count);
            Assert.DoesNotContain(ws.Pending.All, c => c.NeedsInput);
            var outcome = Apply(ws, "enable");

            Assert.True(outcome.Succeeded, string.Join(", ", outcome.NotAsPlanned.Select(c => c.Policy.Uid)));
            Assert.Null(outcome.RefreshError);
            var afterEnable = store.Read();
            Assert.True(afterEnable.Version > original.Version);
            AssertVersionsRose(original, afterEnable);
            AssertOracleAgrees(ws, policies);
            foreach (var (pol, scope) in policies)
            {
                var state = ws.States[pol.Uid, scope];
                Assert.Equal(PolicyState.Enabled, state.State);
                Assert.Equal(LiveStatus.Applied, state.Live.Status);
            }
            // Expandable text stays unexpanded in the registry.
            var redirect = registry.ReadKey(Scope.Machine, @"Software\Policies\Microsoft\EventViewer");
            Assert.Contains(redirect, v => v.Type == RegType.ExpandSz && (string?)v.Data == @"%SystemRoot%\gpo-test.exe");
            // The largest DWORD the Reporting policy permits.
            Assert.Contains(ws.States["appv:Reporting_Server_Policy", Scope.Machine].Entries, e => e.Data is 4294967295L);

            // Disabled.
            foreach (var (pol, scope) in policies)
                ws.Stage(ws.Builder.FromConfig(pol, scope, PolicyConfig.Disabled));
            outcome = Apply(ws, "disable");

            Assert.True(outcome.Succeeded, string.Join(", ", outcome.NotAsPlanned.Select(c => c.Policy.Uid)));
            AssertOracleAgrees(ws, policies);
            foreach (var (pol, scope) in policies)
            {
                var state = ws.States[pol.Uid, scope];
                Assert.Equal(PolicyState.Disabled, state.State);
                Assert.Equal(LiveStatus.Applied, state.Live.Status);
            }

            // Not configured again: only the original entries remain.
            ws.Stage(ws.Builder.NotConfigured(policies.Select(p => p.Pol)));
            outcome = Apply(ws, "clear");

            Assert.True(outcome.Succeeded);
            AssertSameEntries(original, store.Read());
            Assert.All(policies, p => Assert.False(ws.States[p.Pol.Uid, p.Scope].IsConfigured, p.Pol.Uid));
            Assert.All(policies, p => Assert.Equal(LiveStatus.None, ws.States[p.Pol.Uid, p.Scope].Live.Status));
            Assert.Equal(3, ws.Backups.List().Count);
        }
        finally
        {
            // Whatever happened above, the computer ends up with the policy it started with.
            var restore = ws.Planner.PlanRestore(original);
            if (restore.HasWork)
                ws.Applier.Apply(restore, "test clean-up", waitForRefresh: true);
            Directory.Delete(data, recursive: true);
        }
        AssertSameEntries(original, store.Read());
    }

    [Fact]
    public void Restoring_a_backup_through_the_api_brings_back_the_exact_entries()
    {
        if (!ElevationHelper.IsElevated)
            Assert.Skip("Needs administrator rights.");
        var store = new ComGpoStore();
        var original = store.Read();
        var data = Path.Combine(Path.GetTempPath(), "GpoCatalogueTests", Guid.NewGuid().ToString("N"));
        var ws = new Workspace(Repo.Catalog, store, new WindowsRegistryReader(), new AppPaths(data));
        try
        {
            var pol = Repo.Catalog["Snmp:SNMP_Communities"];
            ws.Stage(ws.Builder.FromConfig(pol, Scope.Machine, Filled(pol)));
            var backup = Apply(ws, "enable").Backup;
            Assert.True(ws.States[pol.Uid, Scope.Machine].IsConfigured);

            var plan = ws.Planner.PlanRestore(ws.Backups.Load(backup.Id));
            Assert.Equal(pol.Uid, Assert.Single(plan.Changes).Policy.Uid);
            ws.Applier.Apply(plan, "restore", waitForRefresh: true);

            AssertSameEntries(original, store.Read());
            Assert.False(ws.States[pol.Uid, Scope.Machine].IsConfigured);
            Assert.Equal(LiveStatus.None, ws.States[pol.Uid, Scope.Machine].Live.Status);
        }
        finally
        {
            var restore = ws.Planner.PlanRestore(original);
            if (restore.HasWork)
                ws.Applier.Apply(restore, "test clean-up", waitForRefresh: true);
            Directory.Delete(data, recursive: true);
        }
    }

    private static ApplyOutcome Apply(Workspace ws, string note)
    {
        var plan = ws.Planner.Plan(ws.Pending.All);
        Assert.DoesNotContain(ws.Applier.Preflight(plan), o => o.Blocking);
        var watch = Stopwatch.StartNew();
        var outcome = ws.Applier.Apply(plan, note, waitForRefresh: true);
        TestContext.Current.SendDiagnosticMessage($"{note}: applied and settled in {watch.ElapsedMilliseconds} ms");
        ws.Pending.Clear();
        Assert.True(outcome.NotYetApplied.Count == 0,
            "Not applied: " + string.Join(", ", outcome.NotYetApplied.Select(c => c.Policy.Uid)));
        return outcome;
    }

    private static void AssertVersionsRose(GpoSnapshot before, GpoSnapshot after)
    {
        Assert.True((after.Version & 0xFFFF) > (before.Version & 0xFFFF), "computer half of the version");
        Assert.True((after.Version >> 16) > (before.Version >> 16), "user half of the version");
    }

    private static void AssertSameEntries(GpoSnapshot expected, GpoSnapshot actual)
    {
        foreach (var scope in new[] { Scope.Machine, Scope.User })
        {
            Assert.Equal(expected.Entries(scope).Select(e => e.ToString()), actual.Entries(scope).Select(e => e.ToString()));
        }
    }

    /// <summary>The Python pipeline, reading the files Windows saved, sees the same states.</summary>
    private static void AssertOracleAgrees(Workspace ws, List<(PolicyDef Pol, Scope Scope)> policies)
    {
        foreach (var scope in new[] { Scope.Machine, Scope.User })
        {
            var decoded = Python("decode_pol.py", GpoSnapshot.PolPath(ws.States.Store.Folder, scope), scope.ToString())
                .RootElement.GetProperty("policies");
            foreach (var (pol, _) in policies.Where(p => p.Scope == scope))
            {
                var mine = ws.States[pol.Uid, scope];
                var theirs = decoded.GetProperty(pol.Uid)[0];
                Assert.Equal(theirs.GetProperty("state").GetString(), mine.State.ToString());
                Assert.Equal(theirs.GetProperty("summary").GetString(), mine.Summary);
            }
        }
    }

    private static JsonDocument Python(string tool, params string[] arguments)
    {
        var info = new ProcessStartInfo("python")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        info.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        info.ArgumentList.Add(Path.Combine(Repo.Root, "tools", tool));
        foreach (var a in arguments)
            info.ArgumentList.Add(a);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, errors.Result);
        return JsonDocument.Parse(output.Result);
    }
}
