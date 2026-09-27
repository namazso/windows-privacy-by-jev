using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using GpoCatalogue.Core.Model;

namespace GpoCatalogue.Core.Gpo;

/// <summary>
/// gpt.ini of a group policy object: the version that tells Windows a section changed, and the extensions
/// that must process each section.
/// </summary>
public sealed partial class GptIni
{
    public static readonly Guid RegistryExtension = new("35378EAC-683F-11D2-A89A-00C04FBBCFA2");

    // What the Group Policy editor records as the tool that wrote registry settings.
    public static readonly Guid MachineTool = new("0F6B957F-509E-11D1-A7CC-0000F87571E3");
    public static readonly Guid UserTool = new("0F6B957E-509E-11D1-A7CC-0000F87571E3");

    private const string Section = "[General]";
    private readonly List<string> _lines;

    private GptIni(List<string> lines) => _lines = lines;

    /// <summary>Low word counts changes to the computer section, high word changes to the user section.</summary>
    public uint Version
    {
        get => uint.TryParse(Get("Version"), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0;
        set => Set("Version", value.ToString(CultureInfo.InvariantCulture));
    }

    public ushort MachineVersion => (ushort)(Version & 0xFFFF);
    public ushort UserVersion => (ushort)(Version >> 16);

    public static Guid ToolFor(Scope scope) => scope == Scope.Machine ? MachineTool : UserTool;

    public static GptIni Load(string path) =>
        new(File.Exists(path) ? [.. File.ReadAllLines(path)] : [Section, "Version=0"]);

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllLines(temp, _lines, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    public string ExtensionNames(Scope scope) => Get(NamesKey(scope)) ?? "";

    /// <summary>Counts one more change to a section. The counters wrap within their own half.</summary>
    public void Bump(Scope scope)
    {
        var machine = MachineVersion;
        var user = UserVersion;
        if (scope == Scope.Machine)
            machine = (ushort)(machine == ushort.MaxValue ? 1 : machine + 1);
        else
            user = (ushort)(user == ushort.MaxValue ? 1 : user + 1);
        Version = ((uint)user << 16) | machine;
    }

    /// <summary>Lists <paramref name="extension"/> with <paramref name="tool"/> for a section, keeping the groups sorted.</summary>
    public void EnsureExtension(Scope scope, Guid extension, Guid tool)
    {
        var groups = GroupPattern().Matches(ExtensionNames(scope))
            .Select(m => GuidPattern().Matches(m.Value).Select(g => Guid.Parse(g.Value)).ToList())
            .Where(g => g.Count > 0)
            .ToList();
        var group = groups.FirstOrDefault(g => g[0] == extension);
        if (group is null)
            groups.Add(group = [extension]);
        if (!group.Skip(1).Contains(tool))
            group.Add(tool);

        var text = string.Concat(groups.OrderBy(g => Format(g[0]), StringComparer.Ordinal)
            .Select(g => "[" + Format(g[0])
                + string.Concat(g.Skip(1).OrderBy(Format, StringComparer.Ordinal).Select(Format)) + "]"));
        Set(NamesKey(scope), text);
    }

    private static string NamesKey(Scope scope) =>
        scope == Scope.Machine ? "gPCMachineExtensionNames" : "gPCUserExtensionNames";

    private static string Format(Guid g) => g.ToString("B").ToUpperInvariant();

    private string? Get(string name)
    {
        var line = _lines.FirstOrDefault(l => IsSetting(l, name));
        return line?[(line.IndexOf('=') + 1)..].Trim();
    }

    private void Set(string name, string value)
    {
        var text = $"{name}={value}";
        var at = _lines.FindIndex(l => IsSetting(l, name));
        if (at >= 0)
        {
            _lines[at] = text;
            return;
        }
        var section = _lines.FindIndex(l => l.Trim().Equals(Section, StringComparison.OrdinalIgnoreCase));
        if (section < 0)
        {
            _lines.Insert(0, Section);
            section = 0;
        }
        // Version stays last, as Windows writes it.
        var version = _lines.FindIndex(l => IsSetting(l, "Version"));
        _lines.Insert(version > section && name != "Version" ? version : section + 1, text);
    }

    private static bool IsSetting(string line, string name) =>
        line.TrimStart().StartsWith(name + "=", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\[[^\]]*\]")]
    private static partial Regex GroupPattern();

    [GeneratedRegex(@"\{[0-9A-Fa-f-]{36}\}")]
    private static partial Regex GuidPattern();
}
