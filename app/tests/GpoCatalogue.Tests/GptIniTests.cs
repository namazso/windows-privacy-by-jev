using GpoCatalogue.Core.Gpo;
using GpoCatalogue.Core.Model;
using GpoCatalogue.Core.Pol;

namespace GpoCatalogue.Tests;

public class GptIniTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "GpoCatalogueTests", Guid.NewGuid().ToString("N"));

    public GptIniTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Write(params string[] lines)
    {
        var path = Path.Combine(_folder, "gpt.ini");
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void Version_halves_count_separately()
    {
        var ini = GptIni.Load(Write("[General]", "Version=65537"));

        ini.Bump(Scope.Machine);
        Assert.Equal(65538u, ini.Version);
        ini.Bump(Scope.User);
        Assert.Equal(131074u, ini.Version);
        Assert.Equal((2, 2), (ini.MachineVersion, ini.UserVersion));
    }

    [Fact]
    public void A_full_counter_starts_over_without_touching_the_other_half()
    {
        var ini = GptIni.Load(Write("[General]", "Version=196607")); // user 2, machine 65535

        ini.Bump(Scope.Machine);

        Assert.Equal((1, 2), (ini.MachineVersion, ini.UserVersion));
    }

    [Fact]
    public void The_file_of_this_computer_is_left_as_it_is()
    {
        string[] lines =
        [
            "[General]",
            "gPCUserExtensionNames=[{35378EAC-683F-11D2-A89A-00C04FBBCFA2}{0F6B957E-509E-11D1-A7CC-0000F87571E3}]",
            "gPCMachineExtensionNames=[{35378EAC-683F-11D2-A89A-00C04FBBCFA2}{0F6B957F-509E-11D1-A7CC-0000F87571E3}]",
            "Version=65537",
        ];
        var path = Write(lines);
        var ini = GptIni.Load(path);

        ini.EnsureExtension(Scope.Machine, GptIni.RegistryExtension, GptIni.MachineTool);
        ini.EnsureExtension(Scope.User, GptIni.RegistryExtension, GptIni.UserTool);
        ini.Save(path);

        Assert.Equal(lines, File.ReadAllLines(path));
    }

    [Fact]
    public void Extensions_are_added_in_sorted_groups()
    {
        var scripts = new Guid("42B5FAAE-6536-11D2-AE5A-0000F87571E3");
        var scriptsTool = new Guid("40B6664F-4972-11D1-A7CA-0000F87571E3");
        var path = Write("[General]", $"gPCMachineExtensionNames=[{{{scripts}}}{{{scriptsTool}}}]".ToUpperInvariant()
            .Replace("GPCMACHINEEXTENSIONNAMES", "gPCMachineExtensionNames"), "Version=1");
        var ini = GptIni.Load(path);

        ini.EnsureExtension(Scope.Machine, GptIni.RegistryExtension, GptIni.MachineTool);

        Assert.Equal(
            "[{35378EAC-683F-11D2-A89A-00C04FBBCFA2}{0F6B957F-509E-11D1-A7CC-0000F87571E3}]"
            + "[{42B5FAAE-6536-11D2-AE5A-0000F87571E3}{40B6664F-4972-11D1-A7CA-0000F87571E3}]",
            ini.ExtensionNames(Scope.Machine));
    }

    [Fact]
    public void A_missing_file_starts_at_zero()
    {
        var path = Path.Combine(_folder, "none", "gpt.ini");
        var ini = GptIni.Load(path);

        ini.EnsureExtension(Scope.User, GptIni.RegistryExtension, GptIni.UserTool);
        ini.Bump(Scope.User);
        ini.Save(path);

        Assert.Equal(
        [
            "[General]",
            "gPCUserExtensionNames=[{35378EAC-683F-11D2-A89A-00C04FBBCFA2}{0F6B957E-509E-11D1-A7CC-0000F87571E3}]",
            "Version=65536",
        ], File.ReadAllLines(path));
    }

    [Fact]
    public void An_emptied_or_damaged_policy_file_does_not_stop_reading()
    {
        var machine = GpoSnapshot.PolPath(_folder, Scope.Machine);
        var user = GpoSnapshot.PolPath(_folder, Scope.User);
        Directory.CreateDirectory(Path.GetDirectoryName(machine)!);
        Directory.CreateDirectory(Path.GetDirectoryName(user)!);
        File.WriteAllBytes(machine, []);
        File.WriteAllText(user, "not a policy file");

        var snapshot = GpoSnapshot.Read(_folder);

        Assert.Empty(snapshot.Entries(Scope.Machine));
        Assert.Null(snapshot[Scope.Machine].Error);
        Assert.Empty(snapshot.Entries(Scope.User));
        Assert.NotNull(snapshot[Scope.User].Error);
    }

    [Fact]
    public void Merging_moves_a_clearing_marker_ahead_of_its_key()
    {
        PolEntry[] existing =
        [
            PolEntry.Dword(@"Software\Policies\A", "One", 1),
            PolEntry.Text(@"Software\Policies\List", "Kept", "x"),
        ];
        PolEntry[] added =
        [
            PolEntry.Dword(@"Software\Policies\List\..\List".Replace(@"\..\List", ""), "Flag", 1),
            PolEntry.DeleteValues(@"Software\Policies\List"),
            PolEntry.Text(@"Software\Policies\List", "1", "a"),
        ];

        var merged = SectionState.Merge(existing, added);

        Assert.Equal(["One", "**delvals.", "Kept", "Flag", "1"], merged.Select(e => e.ValueName));
    }

    [Fact]
    public void Reading_a_file_in_order_lets_a_clearing_marker_wipe_what_came_before()
    {
        PolEntry[] file =
        [
            PolEntry.Text(@"Software\Policies\List", "Old", "x"),
            PolEntry.Dword(@"Software\Policies\A", "One", 1),
            PolEntry.DeleteValues(@"Software\Policies\List"),
            PolEntry.Text(@"Software\Policies\List", "1", "a"),
        ];

        Assert.Equal(["One", "**delvals.", "1"], SectionState.Canonicalize(file).Select(e => e.ValueName));
    }
}
