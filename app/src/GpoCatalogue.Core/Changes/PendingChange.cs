using System.Text.Json;
using System.Text.Json.Serialization;
using GpoCatalogue.Core.Model;
using GpoCatalogue.Core.Pol;

namespace GpoCatalogue.Core.Changes;

public enum ChangeSource
{
    Manual,
    SecurityBaseline,
    Rtlfb,
    ModelPrivacy,
    ModelSecurity,
    NotConfigured,
    Restore,
}

public static class ChangeSourceExtensions
{
    public static string Display(this ChangeSource source) => source switch
    {
        ChangeSource.Manual => "Edited",
        ChangeSource.SecurityBaseline => "Security Baseline",
        ChangeSource.Rtlfb => "RTLFB",
        ChangeSource.ModelPrivacy => "Model (privacy)",
        ChangeSource.ModelSecurity => "Model (security)",
        ChangeSource.NotConfigured => "Not configured",
        ChangeSource.Restore => "Backup",
        _ => source.ToString(),
    };
}

/// <summary>A change to one policy in one section that has been decided on but not written yet.</summary>
public sealed class PendingChange
{
    public required string Uid { get; init; }
    public required Scope Scope { get; init; }
    public required ChangeSource Source { get; init; }

    /// <summary>The entries the policy is to have; empty means Not configured.</summary>
    public required IReadOnlyList<PolEntry> Target { get; init; }

    /// <summary>What has to be filled in before the change can be written; empty when it is complete.</summary>
    public IReadOnlyList<string> Missing { get; init; } = [];

    /// <summary>True when the option values are the template's defaults rather than anyone's choice.</summary>
    public bool TemplateDefaults { get; init; }

    /// <summary>Identifies the entries the policy had when the change was staged.</summary>
    public string BaseHash { get; init; } = "";

    /// <summary>The state that was asked for, when the change came from a state rather than from entries.</summary>
    public PolicyState? Intended { get; init; }

    public bool NeedsInput => Missing.Count > 0;
}

/// <summary>The changes waiting to be applied, at most one per policy and section.</summary>
public sealed class PendingStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Dictionary<(string, Scope), PendingChange> _changes = [];

    public event EventHandler? Changed;

    public int Count => _changes.Count;

    public IReadOnlyCollection<PendingChange> All => _changes.Values;

    public PendingChange? Get(string uid, Scope scope) => _changes.GetValueOrDefault((uid, scope));

    public IEnumerable<PendingChange> For(PolicyDef pol) =>
        pol.Scopes.Select(s => Get(pol.Uid, s)).OfType<PendingChange>();

    public void Set(PendingChange change) => Update(() => _changes[(change.Uid, change.Scope)] = change);

    public void SetMany(IEnumerable<PendingChange> changes) => Update(() =>
    {
        foreach (var c in changes)
            _changes[(c.Uid, c.Scope)] = c;
    });

    public void Remove(string uid, Scope scope) => Update(() => _changes.Remove((uid, scope)));

    public void RemoveMany(IEnumerable<PendingChange> changes) => Update(() =>
    {
        foreach (var c in changes.ToList())
            _changes.Remove((c.Uid, c.Scope));
    });

    public void Clear() => Update(_changes.Clear);

    public void Save(string path)
    {
        if (_changes.Count == 0)
        {
            File.Delete(path);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var records = _changes.Values.Select(c => new Record
        {
            Uid = c.Uid,
            Scope = c.Scope,
            Source = c.Source,
            Target = c.Target.Select(e => e.ToRaw()).ToList(),
            Missing = [.. c.Missing],
            TemplateDefaults = c.TemplateDefaults,
            BaseHash = c.BaseHash,
            Intended = c.Intended,
        });
        File.WriteAllText(path, JsonSerializer.Serialize(records, Json));
    }

    /// <summary>Adds the changes saved in a file, skipping policies the catalogue no longer has.</summary>
    public int Load(string path, CatalogStore catalog)
    {
        if (!File.Exists(path))
            return 0;
        List<Record>? records;
        try
        {
            records = JsonSerializer.Deserialize<List<Record>>(File.ReadAllText(path), Json);
        }
        catch (JsonException)
        {
            return 0;
        }
        var loaded = (records ?? [])
            .Where(r => catalog.Find(r.Uid)?.Scopes.Contains(r.Scope) == true)
            .Select(r => new PendingChange
            {
                Uid = r.Uid,
                Scope = r.Scope,
                Source = r.Source,
                Target = r.Target.Select(PolEntry.FromRaw).ToList(),
                Missing = r.Missing,
                TemplateDefaults = r.TemplateDefaults,
                BaseHash = r.BaseHash,
                Intended = r.Intended,
            })
            .ToList();
        SetMany(loaded);
        return loaded.Count;
    }

    private void Update(Action action)
    {
        action();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class Record
    {
        public string Uid { get; set; } = "";
        public Scope Scope { get; set; }
        public ChangeSource Source { get; set; }
        public List<RawEntry> Target { get; set; } = [];
        public List<string> Missing { get; set; } = [];
        public bool TemplateDefaults { get; set; }
        public string BaseHash { get; set; } = "";
        public PolicyState? Intended { get; set; }
    }
}
