using GpoCatalogue.Core.Changes;
using GpoCatalogue.Core.Encode;
using GpoCatalogue.Core.Gpo;
using GpoCatalogue.Core.Model;
using GpoCatalogue.Core.Pol;
using GpoCatalogue.Core.View;

namespace GpoCatalogue.Tests;

public class ChangeTests
{
    private const string DefenderKey = @"Software\Policies\Microsoft\Windows Defender";
    private const string Defender = "WindowsDefender:DisableAntiSpywareDefender";
    private static readonly PolEntry DefenderEntry = PolEntry.Dword(DefenderKey, "DisableAntiSpyware", 1);

    private static ApplyOutcome ApplyAll(TestWorkspace t)
    {
        var plan = t.Workspace.Planner.Plan(t.Workspace.Pending.All);
        var outcome = t.Workspace.Applier.Apply(plan, "test", waitForRefresh: false);
        t.Workspace.Pending.Clear();
        return outcome;
    }

    [Fact]
    public void Staging_changes_nothing_until_applied()
    {
        using var t = new TestWorkspace(machine: [DefenderEntry]);
        var pol = Repo.Catalog["Conf:DisableChat"];

        t.Workspace.Stage(t.Workspace.Builder.FromConfig(pol, Scope.User, PolicyConfig.Enabled()));

        Assert.Equal(1, t.Workspace.Pending.Count);
        Assert.Equal("Enabled", t.Row(pol.Uid).PendingText);
        Assert.Equal("Not configured", t.Row(pol.Uid).StateText);
        Assert.False(File.Exists(GpoSnapshot.PolPath(t.GpoFolder, Scope.User)));
    }

    [Fact]
    public void Applying_writes_the_change_and_keeps_everything_else()
    {
        using var t = new TestWorkspace(machine: [DefenderEntry]);
        var chat = Repo.Catalog["Conf:DisableChat"];
        var telemetry = Repo.Catalog["DataCollection:AllowTelemetry"];
        t.Workspace.Stage(t.Workspace.Builder.FromConfig(chat, Scope.User, PolicyConfig.Enabled()));
        t.Workspace.Stage(t.Workspace.Builder.FromConfig(telemetry, Scope.Machine, PolicyConfig.Enabled(
            new Dictionary<string, OptionValue> { ["AllowTelemetry"] = new EnumOption(0) })));

        var outcome = ApplyAll(t);

        Assert.True(outcome.Succeeded);
        Assert.Equal("Enabled", t.Row(chat.Uid).StateText);
        Assert.Equal("Computer: Enabled; Allow Diagnostic Data = Diagnostic data off (not recommended) | "
                     + "User: Not configured", t.Row(telemetry.Uid).StateText);
        Assert.Equal("Enabled", t.Row(Defender).StateText);
        var machine = PolFile.Read(GpoSnapshot.PolPath(t.GpoFolder, Scope.Machine));
        Assert.Equal(["DisableAntiSpyware", "AllowTelemetry"], machine.Select(e => e.ValueName));
        Assert.Single(PolFile.Read(GpoSnapshot.PolPath(t.GpoFolder, Scope.User)));
    }

    [Fact]
    public void Applying_counts_a_change_for_each_section_written()
    {
        using var t = new TestWorkspace(machine: [DefenderEntry]);
        File.WriteAllLines(GpoSnapshot.IniPath(t.GpoFolder), ["[General]", "Version=65537"]);
        t.Workspace.Stage(t.Workspace.Builder.FromConfig(Repo.Catalog["Conf:DisableChat"], Scope.User, PolicyConfig.Enabled()));

        ApplyAll(t);

        var ini = GptIni.Load(GpoSnapshot.IniPath(t.GpoFolder));
        Assert.Equal((1, 2), (ini.MachineVersion, ini.UserVersion));
        Assert.Equal("[{35378EAC-683F-11D2-A89A-00C04FBBCFA2}{0F6B957E-509E-11D1-A7CC-0000F87571E3}]",
            ini.ExtensionNames(Scope.User));
        Assert.Equal("", ini.ExtensionNames(Scope.Machine));
    }

    [Fact]
    public void Not_configured_removes_the_entries_of_the_policy_only()
    {
        using var t = new TestWorkspace(machine:
        [
            DefenderEntry,
            PolEntry.Dword(@"Software\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0),
            PolEntry.Dword(@"Software\Policies\Example", "Unknown", 7),
        ]);

        t.Workspace.Stage(t.Workspace.Builder.NotConfigured([Repo.Catalog["DataCollection:AllowTelemetry"]]));
        var outcome = ApplyAll(t);

        Assert.True(outcome.Succeeded);
        Assert.Equal(["DisableAntiSpyware", "Unknown"],
            PolFile.Read(GpoSnapshot.PolPath(t.GpoFolder, Scope.Machine)).Select(e => e.ValueName));
    }

    [Fact]
    public void Asking_for_what_is_already_there_stages_nothing()
    {
        using var t = new TestWorkspace(machine: [DefenderEntry]);

        var result = t.Workspace.Builder.FromConfig(Repo.Catalog[Defender], Scope.Machine, PolicyConfig.Enabled());
        t.Workspace.Stage(result);

        Assert.Empty(result.Staged);
        Assert.Equal(1, result.Count(SkipReason.AlreadySet));
        Assert.Equal(0, t.Workspace.Pending.Count);
    }

    [Fact]
    public void Asking_for_the_current_state_withdraws_an_earlier_change()
    {
        using var t = new TestWorkspace(machine: [DefenderEntry]);
        var pol = Repo.Catalog[Defender];
        t.Workspace.Stage(t.Workspace.Builder.FromConfig(pol, Scope.Machine, PolicyConfig.Disabled));
        Assert.Equal(1, t.Workspace.Pending.Count);

        t.Workspace.Stage(t.Workspace.Builder.FromConfig(pol, Scope.Machine, PolicyConfig.Enabled()));

        Assert.Equal(0, t.Workspace.Pending.Count);
    }

    [Fact]
    public void Baseline_copy_takes_the_entries_of_the_baseline()
    {
        using var t = new TestWorkspace();
        var policies = t.Workspace.Rows.Select(r => r.Def).ToList();

        var result = t.Workspace.Builder.FromBaseline(policies, ChangeSource.SecurityBaseline);
        t.Workspace.Stage(result);
        var outcome = ApplyAll(t);

        Assert.Equal(266, result.Staged.Count);
        Assert.Equal(3626 - 266, result.Count(SkipReason.NoValue));
        Assert.True(outcome.Succeeded);
        Assert.All(t.Workspace.Rows.Where(r => r.Def.Sb.Count > 0), r => Assert.True(r.MatchesSb, r.Uid));
        // What the program decodes from its own file is what the pipeline decoded from the baseline.
        Assert.All(t.Workspace.Rows.Where(r => r.Def.Sb.Count > 0), r =>
            Assert.Equal(r.Def.Sb.Select(a => a.Summary), r.States.Where(s => s.IsConfigured).Select(s => s.Summary)));
    }

    [Fact]
    public void Both_baselines_together_are_applied_without_loss()
    {
        using var t = new TestWorkspace();
        var policies = t.Workspace.Rows.Select(r => r.Def).ToList();
        t.Workspace.Stage(t.Workspace.Builder.FromBaseline(policies, ChangeSource.Rtlfb));
        ApplyAll(t);

        // Policies both baselines set end up as the one applied last; all others must still match.
        t.Workspace.Stage(t.Workspace.Builder.FromBaseline(policies, ChangeSource.SecurityBaseline));
        var outcome = ApplyAll(t);

        Assert.True(outcome.Succeeded);
        Assert.All(t.Workspace.Rows.Where(r => r.Def.Sb.Count > 0), r => Assert.True(r.MatchesSb, r.Uid));
        var rtlfbOnly = t.Workspace.Rows.Where(r => r.Def.Rtlfb.Count > 0 && r.Def.Sb.Count == 0).ToList();
        Assert.Equal(102, rtlfbOnly.Count);
        var lost = rtlfbOnly.Where(r => r.MatchesRtlfb != true).Select(r => r.Uid).ToList();
        Assert.True(lost.Count == 0, "No longer as RTLFB sets them: " + string.Join(", ", lost));
    }

    [Fact]
    public void Model_copy_follows_the_direction_the_table_shows()
    {
        using var t = new TestWorkspace();
        var rows = t.Workspace.Rows;

        var result = t.Workspace.Builder.FromModel(rows.Select(r => r.Def), ChangeSource.ModelPrivacy);

        var none = rows.Count(r => r.PrivacyDirection == PolicyRow.NoDirection);
        Assert.Equal(none, result.Count(SkipReason.NoValue));
        Assert.Equal(rows.Count - none, result.Staged.Count + result.Count(SkipReason.AlreadySet));
        foreach (var change in result.Staged.Where(c => !c.NeedsInput))
        {
            var row = t.Row(change.Uid);
            var decoded = t.Workspace.States.Decoder.Decode(row.Def, change.Scope, change.Target);
            // A few templates write an "enabled" that reads back otherwise; those carry the intended state.
            if (decoded?.State.ToString() != row.PrivacyDirection)
                Assert.Equal(row.PrivacyDirection, change.Intended?.ToString());
        }
    }

    [Fact]
    public void Model_copy_marks_default_options_and_missing_input()
    {
        using var t = new TestWorkspace();
        var withOptions = t.Workspace.Rows.First(r =>
            r.PrivacyDirection == "Enabled" && r.Def.Elements.Count > 0
            && !r.Def.Elements.Any(e => e.Required && e.Type == ElementDef.Text));
        var needsText = t.Workspace.Rows.First(r =>
            r.PrivacyDirection == "Enabled" && r.Def.Elements.Any(e =>
                e.Required && e.Type == ElementDef.Text && string.IsNullOrEmpty(e.Ui?.DefStr)));

        var result = t.Workspace.Builder.FromModel([withOptions.Def, needsText.Def], ChangeSource.ModelPrivacy);

        var complete = result.Staged.Single(c => c.Uid == withOptions.Uid);
        var incomplete = result.Staged.Single(c => c.Uid == needsText.Uid);
        Assert.True(complete.TemplateDefaults);
        Assert.True(incomplete.NeedsInput);

        t.Workspace.Stage(result);
        var plan = t.Workspace.Planner.Plan(t.Workspace.Pending.All);
        Assert.False(plan.Changes.Single(c => c.Policy.Uid == needsText.Uid).CanApply);
        Assert.Contains(plan.Changes.Single(c => c.Policy.Uid == withOptions.Uid).Warnings, w => w.Contains("defaults"));

        // What needs input is left out when the rest is applied.
        t.Workspace.Applier.Apply(plan, "test", waitForRefresh: false);
        Assert.False(t.Row(needsText.Uid).IsConfigured);
        Assert.True(t.Row(withOptions.Uid).IsConfigured);
    }

    [Fact]
    public void Model_copy_on_a_policy_with_two_sections_goes_to_the_computer()
    {
        using var t = new TestWorkspace();
        var row = t.Workspace.Rows.First(r => r.Def.Class == "Both" && r.PrivacyDirection == "Disabled");

        var change = Assert.Single(t.Workspace.Builder.FromModel([row.Def], ChangeSource.ModelPrivacy).Staged);

        Assert.Equal(Scope.Machine, change.Scope);
    }

    [Fact]
    public void Review_lists_the_registry_values_a_change_touches()
    {
        using var t = new TestWorkspace(machine:
            [PolEntry.Dword(@"Software\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 3)]);
        var pol = Repo.Catalog["DataCollection:AllowTelemetry"];
        t.Workspace.Stage(t.Workspace.Builder.FromBaseline([pol], ChangeSource.Rtlfb));

        var planned = Assert.Single(t.Workspace.Planner.Plan(t.Workspace.Pending.All).Changes);

        Assert.Equal("Enabled; Allow Diagnostic Data = Send optional diagnostic data", planned.Current);
        Assert.Equal("Enabled; Allow Diagnostic Data = Diagnostic data off (not recommended)", planned.New);
        var diff = Assert.Single(planned.Diffs);
        Assert.Equal(DiffKind.Changed, diff.Kind);
        Assert.Contains("REG_DWORD 3  ->  REG_DWORD 0", diff.Line);
    }

    [Fact]
    public void Review_names_policies_that_change_along()
    {
        using var t = new TestWorkspace();
        var explorer = Repo.Catalog["WindowsExplorer:EnableSmartScreen"];
        var twin = Repo.Catalog["SmartScreen:ShellConfigureSmartScreen"];
        t.Workspace.Stage(t.Workspace.Builder.FromBaseline([explorer], ChangeSource.SecurityBaseline));

        var plan = t.Workspace.Planner.Plan(t.Workspace.Pending.All);

        var effect = Assert.Single(plan.SideEffects);
        Assert.Equal(twin.Uid, effect.Policy.Uid);
        Assert.Equal("Not configured", effect.Before);
        Assert.StartsWith("Enabled", effect.After);
    }

    [Fact]
    public void Review_warns_when_a_value_outside_the_policy_keys_stays_behind()
    {
        var pol = Repo.Catalog["MSS-legacy:Pol_MSS_ScreenSaverGracePeriod"];
        var el = pol.Elements.Single();
        using var t = new TestWorkspace(machine: [PolEntry.Text(el.Key, el.ValueName!, "5")]);
        t.Workspace.Stage(t.Workspace.Builder.NotConfigured([pol]));

        var planned = Assert.Single(t.Workspace.Planner.Plan(t.Workspace.Pending.All).Changes);

        Assert.Contains(planned.Warnings, w => w.Contains("keeps the current value"));
    }

    [Fact]
    public void Review_notices_a_policy_that_changed_after_staging()
    {
        using var t = new TestWorkspace();
        var pol = Repo.Catalog[Defender];
        t.Workspace.Stage(t.Workspace.Builder.FromConfig(pol, Scope.Machine, PolicyConfig.Disabled));
        PolFile.Write(GpoSnapshot.PolPath(t.GpoFolder, Scope.Machine), [DefenderEntry]);
        t.Workspace.States.Refresh();

        var planned = Assert.Single(t.Workspace.Planner.Plan(t.Workspace.Pending.All).Changes);

        Assert.Contains(planned.Warnings, w => w.Contains("changed by something else"));
    }

    [Fact]
    public void Applying_refuses_when_the_files_changed_after_the_review()
    {
        using var t = new TestWorkspace();
        t.Workspace.Stage(t.Workspace.Builder.FromConfig(Repo.Catalog[Defender], Scope.Machine, PolicyConfig.Enabled()));
        var plan = t.Workspace.Planner.Plan(t.Workspace.Pending.All);
        PolFile.Write(GpoSnapshot.PolPath(t.GpoFolder, Scope.Machine), [PolEntry.Dword(@"Software\Policies\X", "Y", 1)]);

        var error = Assert.Throws<GpoException>(() => t.Workspace.Applier.Apply(plan, "test", waitForRefresh: false));

        Assert.Contains("changed by another program", error.Message);
        Assert.Empty(t.Workspace.Backups.List());
    }

    [Fact]
    public void Every_apply_is_backed_up_and_can_be_undone()
    {
        using var t = new TestWorkspace(machine: [DefenderEntry]);
        File.WriteAllLines(GpoSnapshot.IniPath(t.GpoFolder), ["[General]", "Version=65537"]);
        var policies = t.Workspace.Rows.Select(r => r.Def).ToList();
        t.Workspace.Stage(t.Workspace.Builder.FromBaseline(policies, ChangeSource.Rtlfb));
        t.Workspace.Stage(t.Workspace.Builder.NotConfigured([Repo.Catalog[Defender]]));
        var outcome = ApplyAll(t);
        Assert.False(t.Row(Defender).IsConfigured);

        var backup = Assert.Single(t.Workspace.Backups.List());
        Assert.Equal(outcome.Backup.Id, backup.Id);
        Assert.Equal((1, 0, 65537u), (backup.MachineEntries, backup.UserEntries, backup.Version));
        var plan = t.Workspace.Planner.PlanRestore(t.Workspace.Backups.Load(backup.Id));
        Assert.Equal(110, plan.Changes.Count);
        t.Workspace.Applier.Apply(plan, "restore", waitForRefresh: false);

        Assert.Equal([Defender], t.Workspace.Rows.Where(r => r.IsConfigured).Select(r => r.Uid));
        var entry = Assert.Single(PolFile.Read(GpoSnapshot.PolPath(t.GpoFolder, Scope.Machine)));
        Assert.Equal(DefenderEntry.ToString(), entry.ToString());
        Assert.Empty(PolFile.ReadIfExists(GpoSnapshot.PolPath(t.GpoFolder, Scope.User)));
        // Restoring is a change like any other: the version keeps rising and the restore is backed up too.
        Assert.True(GptIni.Load(GpoSnapshot.IniPath(t.GpoFolder)).MachineVersion > 2);
        Assert.Equal(2, t.Workspace.Backups.List().Count);
    }

    [Fact]
    public void Restore_brings_back_entries_of_no_policy()
    {
        var unknown = PolEntry.Dword(@"Software\Policies\Example", "Unknown", 7);
        using var t = new TestWorkspace(machine: [unknown, DefenderEntry]);
        var backup = t.Workspace.Backups.Create(t.Store.Read(), "before");
        PolFile.Write(GpoSnapshot.PolPath(t.GpoFolder, Scope.Machine), [DefenderEntry]);
        t.Workspace.States.Refresh();

        var plan = t.Workspace.Planner.PlanRestore(t.Workspace.Backups.Load(backup.Id));
        t.Workspace.Applier.Apply(plan, "restore", waitForRefresh: false);

        Assert.Empty(plan.Changes);
        Assert.Equal(DiffKind.Added, Assert.Single(plan.OtherDiffs).Kind);
        Assert.Equal(["Unknown", "DisableAntiSpyware"],
            PolFile.Read(GpoSnapshot.PolPath(t.GpoFolder, Scope.Machine)).Select(e => e.ValueName));
    }

    [Fact]
    public void Staged_changes_survive_a_restart()
    {
        using var t = new TestWorkspace();
        var policies = t.Workspace.Rows.Select(r => r.Def).ToList();
        t.Workspace.Stage(t.Workspace.Builder.FromBaseline(policies, ChangeSource.Rtlfb));
        t.Workspace.Stage(t.Workspace.Builder.FromModel(policies.Take(300), ChangeSource.ModelSecurity));
        var before = t.Workspace.Pending.All
            .Select(c => (c.Uid, c.Scope, c.Source, SectionState.Hash(c.Target), c.NeedsInput, c.TemplateDefaults, c.Intended))
            .Order().ToList();
        var path = Path.Combine(t.Folder, "pending.json");

        t.Workspace.Pending.Save(path);
        var loaded = new PendingStore();
        var count = loaded.Load(path, Repo.Catalog);

        Assert.Equal(before.Count, count);
        Assert.Equal(before, loaded.All
            .Select(c => (c.Uid, c.Scope, c.Source, SectionState.Hash(c.Target), c.NeedsInput, c.TemplateDefaults, c.Intended))
            .Order().ToList());
    }

    [Fact]
    public void A_list_that_clears_its_key_is_written_ahead_of_the_values()
    {
        var pol = Repo.Catalog["DeviceInstallation:DeviceInstall_Classes_Deny"];
        var list = pol.Elements.Single(e => e.IsList);
        // A value that was in the key before must not be wiped by a marker written after it.
        using var t = new TestWorkspace(machine: [PolEntry.Text(list.Key, "Other", "x")]);
        var options = OptionReader.Defaults(pol);
        options[list.Id] = new ListOption([new("", "{a}")]);
        t.Workspace.Stage(t.Workspace.Builder.FromConfig(pol, Scope.Machine, PolicyConfig.Enabled(options)));

        ApplyAll(t);

        var inKey = PolFile.Read(GpoSnapshot.PolPath(t.GpoFolder, Scope.Machine))
            .Where(e => Slot.NormKey(e.Key) == Slot.NormKey(list.Key)).ToList();
        Assert.Equal(PolMarker.DelVals, inKey[0].Marker);
        Assert.Equal(1, inKey.Count(e => e.Marker == PolMarker.DelVals));
    }
}
