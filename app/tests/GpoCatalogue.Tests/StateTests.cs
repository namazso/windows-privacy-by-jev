using GpoCatalogue.Core.Live;
using GpoCatalogue.Core.Model;
using GpoCatalogue.Core.Pol;
using GpoCatalogue.Core.View;

namespace GpoCatalogue.Tests;

public class StateTests
{
    private const string DefenderKey = @"Software\Policies\Microsoft\Windows Defender";
    private const string Defender = "WindowsDefender:DisableAntiSpywareDefender";

    [Fact]
    public void Nothing_configured_and_nothing_in_the_registry()
    {
        using var t = new TestWorkspace();

        var state = t.Workspace.States[Defender, Scope.Machine];

        Assert.Equal(PolicyState.NotConfigured, state.State);
        Assert.Equal(LiveStatus.None, state.Live.Status);
        Assert.Equal("Not configured", t.Row(Defender).StateText);
        Assert.Equal("", t.Row(Defender).LiveText);
        Assert.All(t.Workspace.Rows, r => Assert.False(r.IsConfigured));
    }

    [Fact]
    public void Configured_and_present_in_the_registry_is_applied()
    {
        var entry = PolEntry.Dword(DefenderKey, "DisableAntiSpyware", 1);
        using var t = new TestWorkspace(machine: [entry], registry: true);
        t.Registry.Set(Scope.Machine, entry);
        t.Workspace.States.Refresh();

        var state = t.Workspace.States[Defender, Scope.Machine];

        Assert.Equal(PolicyState.Enabled, state.State);
        Assert.Equal(LiveStatus.Applied, state.Live.Status);
        Assert.Equal("Enabled", t.Row(Defender).StateText);
        Assert.Equal("applied", t.Row(Defender).LiveText);
    }

    [Fact]
    public void Configured_but_different_in_the_registry_is_not_applied()
    {
        using var t = new TestWorkspace(machine: [PolEntry.Dword(DefenderKey, "DisableAntiSpyware", 1)], registry: true);
        t.Registry.Set(Scope.Machine, PolEntry.Dword(DefenderKey, "DisableAntiSpyware", 0));
        t.Workspace.States.Refresh();

        Assert.Equal(LiveStatus.NotApplied, t.Workspace.States[Defender, Scope.Machine].Live.Status);
        Assert.Equal("Enabled (not applied)", t.Row(Defender).StateText);
    }

    [Fact]
    public void A_delete_marker_is_applied_when_the_value_is_absent()
    {
        using var t = new TestWorkspace(user: [PolEntry.Delete(@"Software\Policies\Microsoft\Conferencing", "NoChat")], registry: true);

        var state = t.Workspace.States["Conf:DisableChat", Scope.User];

        Assert.Equal(PolicyState.Disabled, state.State);
        Assert.Equal(LiveStatus.Applied, state.Live.Status);
    }

    [Fact]
    public void A_policy_value_nobody_configured_is_registry_only()
    {
        using var t = new TestWorkspace(registry: true);
        t.Registry.Set(Scope.Machine, PolEntry.Dword(DefenderKey, "DisableAntiSpyware", 1));
        t.Workspace.States.Refresh();

        var state = t.Workspace.States[Defender, Scope.Machine];

        Assert.Equal(PolicyState.NotConfigured, state.State);
        Assert.Equal(LiveStatus.RegistryOnly, state.Live.Status);
        Assert.Equal("Enabled (registry only)", t.Row(Defender).StateText);
        Assert.False(t.Row(Defender).IsConfigured);
    }

    [Fact]
    public void A_value_outside_the_policy_keys_says_nothing_about_policy()
    {
        var pol = Repo.Catalog["MSS-legacy:Pol_MSS_ScreenSaverGracePeriod"];
        Assert.True(LiveComparer.LeavesValuesBehind(pol));
        using var t = new TestWorkspace(registry: true);
        t.Registry.Set(Scope.Machine, PolEntry.Text(pol.Elements[0].Key, pol.Elements[0].ValueName!, "5"));
        t.Workspace.States.Refresh();

        Assert.Equal(LiveStatus.OrdinarySetting, t.Workspace.States[pol.Uid, Scope.Machine].Live.Status);
        Assert.Equal("Not configured", t.Row(pol.Uid).StateText);
        Assert.Equal("value present", t.Row(pol.Uid).LiveText);
    }

    [Fact]
    public void A_policy_with_two_sections_names_them_once_one_is_configured()
    {
        var pol = Repo.Catalog["WindowsAnytimeUpgrade:Disabled"];
        Assert.Equal("Both", pol.Class);
        using var t = new TestWorkspace(user: [PolEntry.Dword(pol.RegistryKey, pol.ValueName!, 1)]);

        Assert.Equal("Computer: Not configured | User: Enabled", t.Row(pol.Uid).StateText);
        Assert.True(t.Row(pol.Uid).IsConfigured);
    }

    [Fact]
    public void Entries_of_no_policy_are_kept_apart()
    {
        using var t = new TestWorkspace(machine:
        [
            PolEntry.Dword(@"Software\Policies\Example\Nothing", "Unknown", 1),
            PolEntry.Dword(DefenderKey, "DisableAntiSpyware", 1),
        ]);

        var unmapped = Assert.Single(t.Workspace.States.Unmapped(Scope.Machine));

        Assert.Equal("Unknown", unmapped.ValueName);
    }

    [Fact]
    public void Later_entries_replace_earlier_ones()
    {
        using var t = new TestWorkspace(machine:
        [
            PolEntry.Dword(DefenderKey, "DisableAntiSpyware", 1),
            PolEntry.Delete(DefenderKey, "DisableAntiSpyware"),
        ]);

        Assert.Equal(PolicyState.Disabled, t.Workspace.States[Defender, Scope.Machine].State);
        Assert.Single(t.Workspace.States[Defender, Scope.Machine].Entries);
    }

    [Fact]
    public void State_filter_finds_configured_policies()
    {
        using var t = new TestWorkspace(machine: [PolEntry.Dword(DefenderKey, "DisableAntiSpyware", 1)], registry: true);
        t.Registry.Set(Scope.Machine, PolEntry.Dword(@"Software\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0));
        t.Workspace.States.Refresh();
        var view = t.Workspace.View;

        view.Filter(Columns.State).Mode = Columns.StateConfigured;
        Assert.Equal([Defender], t.Workspace.Compute().Select(r => r.Uid));

        view.Filter(Columns.State).Mode = Columns.StateEnabled;
        Assert.Equal([Defender], t.Workspace.Compute().Select(r => r.Uid));

        view.Filter(Columns.State).Mode = Columns.StateRegistryOnly;
        Assert.Contains("DataCollection:AllowTelemetry", t.Workspace.Compute().Select(r => r.Uid));

        view.Filter(Columns.State).Mode = Columns.StateNotConfigured;
        Assert.Equal(3625, t.Workspace.Compute().Count);
    }

    [Fact]
    public void Baseline_filter_compares_with_the_current_state()
    {
        var pol = Repo.Catalog["DataCollection:AllowTelemetry"];
        var baseline = pol.Rtlfb.Single().Entries.Select(PolEntry.FromRaw).ToList();
        using var t = new TestWorkspace(machine: baseline);
        var view = t.Workspace.View;

        Assert.True(t.Row(pol.Uid).MatchesRtlfb);
        Assert.Null(t.Row(StateTests.Defender).MatchesRtlfb);

        view.Filter(Columns.Rtlfb).Mode = Columns.ModeMatch;
        Assert.Equal([pol.Uid], t.Workspace.Compute().Select(r => r.Uid));

        view.Filter(Columns.Rtlfb).Mode = Columns.ModeDiffer;
        Assert.Equal(108, t.Workspace.Compute().Count);
    }

    [Fact]
    public void Letter_case_of_keys_and_names_does_not_matter()
    {
        using var t = new TestWorkspace(machine: [PolEntry.Dword(DefenderKey.ToUpperInvariant(), "DISABLEANTISPYWARE", 1)], registry: true);
        t.Registry.Set(Scope.Machine, PolEntry.Dword(DefenderKey.ToLowerInvariant(), "disableantispyware", 1));
        t.Workspace.States.Refresh();

        Assert.Equal(LiveStatus.Applied, t.Workspace.States[Defender, Scope.Machine].Live.Status);
    }
}
