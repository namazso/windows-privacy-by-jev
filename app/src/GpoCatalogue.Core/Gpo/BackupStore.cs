using System.Globalization;
using System.Text.Json;
using GpoCatalogue.Core.Model;

namespace GpoCatalogue.Core.Gpo;

public sealed class BackupInfo
{
    public string Id { get; set; } = "";
    public DateTime Created { get; set; }
    public string Note { get; set; } = "";
    public string Source { get; set; } = "";
    public uint Version { get; set; }
    public int MachineEntries { get; set; }
    public int UserEntries { get; set; }
}

/// <summary>Copies of the policy files taken before each change, so a change can be undone.</summary>
public sealed class BackupStore(string root)
{
    private const string Manifest = "manifest.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Root { get; } = root;

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GpoCatalogue", "Backups");

    public BackupInfo Create(GpoSnapshot snapshot, string note)
    {
        var now = DateTime.Now;
        var id = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        for (var n = 2; Directory.Exists(Path.Combine(Root, id)); n++)
            id = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + n;
        var folder = Path.Combine(Root, id);
        Directory.CreateDirectory(folder);

        Copy(GpoSnapshot.IniPath(snapshot.Folder), GpoSnapshot.IniPath(folder));
        foreach (var scope in new[] { Scope.Machine, Scope.User })
            Copy(GpoSnapshot.PolPath(snapshot.Folder, scope), GpoSnapshot.PolPath(folder, scope));

        var info = new BackupInfo
        {
            Id = id,
            Created = now,
            Note = note,
            Source = snapshot.Folder,
            Version = snapshot.Version,
            MachineEntries = snapshot.Entries(Scope.Machine).Count,
            UserEntries = snapshot.Entries(Scope.User).Count,
        };
        File.WriteAllText(Path.Combine(folder, Manifest), JsonSerializer.Serialize(info, Json));
        return info;
    }

    /// <summary>Newest first.</summary>
    public List<BackupInfo> List()
    {
        if (!Directory.Exists(Root))
            return [];
        var backups = new List<BackupInfo>();
        foreach (var folder in Directory.EnumerateDirectories(Root))
        {
            var manifest = Path.Combine(folder, Manifest);
            if (!File.Exists(manifest))
                continue;
            try
            {
                if (JsonSerializer.Deserialize<BackupInfo>(File.ReadAllText(manifest)) is { } info)
                {
                    info.Id = Path.GetFileName(folder);
                    backups.Add(info);
                }
            }
            catch (JsonException)
            {
                // A damaged manifest hides that backup; the files stay where they are.
            }
        }
        return backups.OrderByDescending(b => b.Created).ToList();
    }

    public GpoSnapshot Load(string id) => GpoSnapshot.Read(Path.Combine(Root, id));

    private static void Copy(string from, string to)
    {
        if (!File.Exists(from))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(from, to, overwrite: true);
    }
}
