using System.Diagnostics;
using System.Text.Json;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using GpoCatalogue.Core;
using GpoCatalogue.Core.Gpo;
using GpoCatalogue.Core.Live;
using GpoCatalogue.Core.Model;
using GpoCatalogue.Core.Pol;
using GpoCatalogue.Core.View;
using Microsoft.Win32;

namespace GpoCatalogue.UiTests;

[CollectionDefinition("UI", DisableParallelization = true)]
public sealed class UiCollection;

/// <summary>Rows the table should show for a view, worked out with the same engine outside the program.</summary>
internal static class Expected
{
    private static readonly Lazy<Workspace> Ws = new(() =>
    {
        var folder = Paths.NewTemp("expected");
        return new Workspace(CatalogStore.LoadEmbedded(), new FileGpoStore(folder), new NoRegistry(), new AppPaths(folder));
    });

    public static int Count(Action<ViewState> setup)
    {
        var ws = Ws.Value;
        ws.View.Reset(ws.Columns);
        setup(ws.View);
        return ws.Compute().Count;
    }
}

/// <summary>The NetMeeting bandwidth policy the editing tests use: one number, user section.</summary>
internal static class Throughput
{
    private static readonly Lazy<GpoCatalogue.Core.Model.ElementDef> Element =
        new(() => CatalogStore.LoadEmbedded()["Conf:SetAVThroughput"].Elements.Single());

    public static string Summary(long value) => $"Enabled; {Element.Value.Label} = {value}";

    public static object? Live() => Registry.CurrentUser.OpenSubKey(Element.Value.Key)?.GetValue(Element.Value.ValueName);
}

internal static class Oracle
{
    /// <summary>What the Python pipeline makes of a policy file: uid to summary.</summary>
    public static Dictionary<string, string> Decode(string pol, Scope scope)
    {
        if (!File.Exists(pol))
            return [];
        var info = new ProcessStartInfo("python")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        info.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        info.ArgumentList.Add(Path.Combine(Paths.Root, "tools", "decode_pol.py"));
        info.ArgumentList.Add(pol);
        info.ArgumentList.Add(scope.ToString());
        using var p = Process.Start(info)!;
        var output = p.StandardOutput.ReadToEndAsync();
        var errors = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, errors.Result);
        return JsonDocument.Parse(output.Result).RootElement.GetProperty("policies").EnumerateObject()
            .ToDictionary(e => e.Name, e => e.Value[0].GetProperty("summary").GetString()!);
    }

    public static List<string> Entries(string folder, Scope scope) =>
        SectionState.Canonicalize(PolFile.ReadIfExists(GpoSnapshot.PolPath(folder, scope))).Select(e => e.ToString()).ToList();
}

[Collection("UI")]
public class ReadOnlyUiTests
{
    [Fact]
    public void Browsing_works_like_the_website()
    {
        var data = Paths.NewTemp("data");
        using var d = AppDriver.Start("--read-only", "--data", data, "--theme", "light");

        Assert.StartsWith("Read-only", d.Text("bannerText"));
        Assert.Equal(3626, d.Count());

        d.Search("cortana", Expected.Count(v => v.Search = "cortana"));
        d.Screenshot("1-search");

        // A preset replaces the search and the filters.
        d.SelectComboItem("Quick presets", Preset.All[1].Label);
        d.WaitUntil(() => d.Count() == 273, "the privacy preset");
        Assert.Equal("", d.Text("Search all policies"));

        var withName = Expected.Count(v =>
        {
            Preset.All[1].Apply(v);
            v.Filter(Columns.Name).Text = "telemetry";
        });
        d.Type("filter_name", "telemetry");
        d.WaitUntil(() => d.Count() == withName, $"{withName} rows with the name filter");

        // A hidden column's filter appears when the column is shown.
        Assert.True(d.TryFind("filter_admx") is null || d.Find("filter_admx").IsOffscreen);
        d.ChooseMenuItem("Columns", "Template");
        d.WaitUntil(() => !d.Find("filter_admx").IsOffscreen, "the Template filter to show");

        // Sorting by clicking a header.
        var headerRow = d.Find("grid").FindFirstChild(cf => cf.ByName("Top Row"));
        var header = headerRow?.FindFirstChild(cf => cf.ByName("Security"));
        if (header is not null)
            AppDriver.Act(header);
        d.Screenshot("2-preset-filter-sort");

        // Nothing can be applied in read-only mode, but staging and review still work.
        d.Search("", Expected.Count(v =>
        {
            Preset.All[1].Apply(v);
            v.Filter(Columns.Name).Text = "telemetry";
        }));
        d.SelectAllRows();
        d.ChooseMenuItem("Copy from", "Model recommendation (privacy)");
        d.WaitUntil(() => d.PendingStatus().Contains("staged"), "changes to be staged");
        d.Invoke("Review and apply");
        var review = d.Dialog("Review and apply");
        Assert.False(d.Find("apply", review).IsEnabled);
        d.Screenshot("3-review-read-only", review);
        review.Close();

        // Staged changes are kept for the next session.
        Assert.True(File.Exists(Path.Combine(data, "pending.json")));
        d.Invoke("Discard");
        d.WaitUntil(() => d.PendingStatus() == "No staged changes", "the changes to be discarded");
    }

    [Fact]
    public void Dark_theme_and_detail_pane()
    {
        using var d = AppDriver.Start("--read-only", "--data", Paths.NewTemp("data"), "--theme", "dark",
            "--select", "DataCollection:AllowTelemetry");

        d.WaitUntil(() => d.Text("detailLeft").Contains("Allow Diagnostic Data"), "the detail pane");
        Assert.Contains("Diagnostic data off (not recommended)", d.Text("detailRight"));
        d.Screenshot("4-dark-detail");
    }
}

/// <summary>Editing against a copy of the policy folder: nothing here touches the policy of this computer.</summary>
[Collection("UI")]
public class TestFolderUiTests
{
    [Fact]
    public void Edit_copy_review_apply_and_restore()
    {
        var gpo = Paths.NewTemp("gpo");
        var original = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "GroupPolicy");
        foreach (var file in Directory.EnumerateFiles(original, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(gpo, Path.GetRelativePath(original, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        var machineBefore = Oracle.Entries(gpo, Scope.Machine);
        var userBefore = Oracle.Entries(gpo, Scope.User);

        using var d = AppDriver.Start("--gpo-root", gpo, "--data", Paths.NewTemp("data"), "--theme", "light",
            "--select", "Conf:SetAVThroughput");
        Assert.StartsWith("Test folder", d.Text("bannerText"));

        // The edit dialog, as in the Group Policy editor.
        d.Invoke("Edit");
        var edit = d.Dialog("Edit policy");
        d.Invoke("enabled", edit);
        d.Type("opt_AVThroughput", "100000", edit);
        d.Screenshot("5-edit", edit);
        d.Invoke("stage", edit);
        d.WaitUntil(() => d.PendingStatus().StartsWith("1 staged"), "one staged change");

        // A one-click copy for several policies at once.
        var smartScreen = Expected.Count(v => v.Search = "smartscreen");
        d.Search("smartscreen", smartScreen);
        d.SelectAllRows();
        d.ChooseMenuItem("Copy from", "Security Baseline");
        d.WaitUntil(() => d.Status().Contains("staged from Security Baseline"), "the baseline copy");
        d.Screenshot("6-staged");

        d.Invoke("Review and apply");
        var review = d.Dialog("Review and apply");
        Assert.True(d.Find("apply", review).IsEnabled);
        Assert.Contains("test folder", d.Text("summary", review));
        d.Screenshot("7-review", review);
        d.Invoke("apply", review);
        Assert.Contains("Applied", d.AcceptMessage(review));
        d.WaitUntil(() => d.PendingStatus() == "No staged changes", "the list of staged changes to empty");

        // What the program wrote, read by the Python pipeline.
        var user = Oracle.Decode(GpoSnapshot.PolPath(gpo, Scope.User), Scope.User);
        Assert.Equal(Throughput.Summary(100000), user.GetValueOrDefault("Conf:SetAVThroughput"));
        var machine = Oracle.Decode(GpoSnapshot.PolPath(gpo, Scope.Machine), Scope.Machine);
        var catalog = CatalogStore.LoadEmbedded();
        foreach (var pol in catalog.Policies.Where(p => p.Sb.Count > 0 && (p.Name + p.CategoryPath + p.Explanation).Contains("SmartScreen", StringComparison.OrdinalIgnoreCase)))
            Assert.Equal(pol.Sb[0].Summary, machine.GetValueOrDefault(pol.Uid));
        Assert.Contains(machineBefore[0], Oracle.Entries(gpo, Scope.Machine));

        // Undo through the backups dialog.
        d.Invoke("Backups");
        var backups = d.Dialog("Backups");
        d.Find("backupList", backups).FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem))!.Patterns.SelectionItem.Pattern.Select();
        d.Invoke("restore", backups);
        var restore = d.Dialog("Restore backup");
        d.Invoke("apply", restore);
        Assert.Contains("Applied", d.AcceptMessage(restore));

        Assert.Equal(machineBefore, Oracle.Entries(gpo, Scope.Machine));
        Assert.Equal(userBefore, Oracle.Entries(gpo, Scope.User));
    }
}

/// <summary>The same flow on the real local group policy. Runs only with administrator rights.</summary>
[Collection("UI")]
public class ElevatedUiTests
{

    [Fact]
    public void Changing_and_restoring_the_real_policy()
    {
        if (!ElevationHelper.IsElevated)
            Assert.Skip("Needs administrator rights.");
        var folder = new ComGpoStore().Folder;
        var machineBefore = Oracle.Entries(folder, Scope.Machine);
        var userBefore = Oracle.Entries(folder, Scope.User);
        Assert.Null(Throughput.Live());
        var data = Paths.NewTemp("data");

        using var d = AppDriver.Start("--data", data, "--theme", "light", "--select", "Conf:SetAVThroughput");
        Assert.Contains("(Administrator)", d.Window.Title);
        Assert.True(d.TryFind("bannerText") is null || d.Find("bannerText").IsOffscreen);

        d.Invoke("Edit");
        var edit = d.Dialog("Edit policy");
        d.Invoke("enabled", edit);
        d.Type("opt_AVThroughput", "123456", edit);
        d.Invoke("stage", edit);
        d.Invoke("Review and apply");
        var review = d.Dialog("Review and apply");
        Assert.Contains("local group policy of this computer", d.Text("summary", review));
        d.Invoke("apply", review);
        Assert.Contains("Applied", d.AcceptMessage(review));

        // Written to the policy file, applied to the registry, and shown as such.
        Assert.Equal(Throughput.Summary(123456),
            Oracle.Decode(GpoSnapshot.PolPath(folder, Scope.User), Scope.User)["Conf:SetAVThroughput"]);
        Assert.Equal(123456, Throughput.Live());
        d.WaitUntil(() => d.Text("detailLeft").Contains("The registry holds these values."), "the detail pane to show the new state");
        d.Screenshot("8-real-policy-applied");

        d.Invoke("Backups");
        var backups = d.Dialog("Backups");
        d.Find("backupList", backups).FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem))!.Patterns.SelectionItem.Pattern.Select();
        d.Invoke("restore", backups);
        var restore = d.Dialog("Restore backup");
        d.Invoke("apply", restore);
        Assert.Contains("Applied", d.AcceptMessage(restore));

        Assert.Equal(machineBefore, Oracle.Entries(folder, Scope.Machine));
        Assert.Equal(userBefore, Oracle.Entries(folder, Scope.User));
        Assert.Null(Throughput.Live());
    }

    /// <summary>Started without administrator rights from an elevated session, the program is read-only.</summary>
    [Fact]
    public void Without_administrator_rights_the_program_offers_to_restart()
    {
        if (!ElevationHelper.IsElevated)
            Assert.Skip("Started unelevated by the test run itself; covered by ordinary runs.");
        const string task = "GpoCatalogueUiTest";
        var data = Paths.NewTemp("data");
        var before = Process.GetProcessesByName("GpoCatalogue").Select(p => p.Id).ToHashSet();
        // A scheduled task with the limited token of the signed-in user is the documented way down from elevation.
        Run("schtasks", "/create", "/tn", task, "/tr", $"\"{AppDriver.Exe}\" --data \"{data}\" --theme light",
            "/sc", "once", "/st", "23:59", "/rl", "LIMITED", "/it", "/f");
        try
        {
            Run("schtasks", "/run", "/tn", task);
            Process? started = null;
            var watch = Stopwatch.StartNew();
            while (started is null && watch.Elapsed < TimeSpan.FromSeconds(20))
            {
                started = Process.GetProcessesByName("GpoCatalogue").FirstOrDefault(p => !before.Contains(p.Id));
                Thread.Sleep(200);
            }
            Assert.NotNull(started);
            using var d = AppDriver.Attach(Application.Attach(started));

            Assert.DoesNotContain("(Administrator)", d.Window.Title);
            Assert.StartsWith("Read-only: not running as administrator", d.Text("bannerText"));
            Assert.True(d.Find("elevate").IsEnabled);
            d.Screenshot("9-not-elevated");
        }
        finally
        {
            Run("schtasks", "/delete", "/tn", task, "/f");
        }
    }

    private static void Run(string file, params string[] arguments)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in arguments)
            info.ArgumentList.Add(a);
        using var p = Process.Start(info)!;
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"{file} {string.Join(' ', arguments)}: {p.StandardError.ReadToEnd()}");
    }
}
