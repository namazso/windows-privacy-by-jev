using GpoCatalogue.Core.Changes;
using GpoCatalogue.Core.Gpo;
using GpoCatalogue.Core.Live;
using GpoCatalogue.Core.State;

namespace GpoCatalogue.Core.View;

/// <summary>Where the program keeps its own files.</summary>
public sealed class AppPaths
{
    public AppPaths(string? dataFolder)
    {
        DataFolder = dataFolder is null
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GpoCatalogue")
            : Path.GetFullPath(dataFolder);
        Backups = dataFolder is null ? BackupStore.DefaultRoot : Path.Combine(DataFolder, "Backups");
    }

    public string DataFolder { get; }
    public string View => Path.Combine(DataFolder, "view.json");
    public string Pending => Path.Combine(DataFolder, "pending.json");
    public string Backups { get; }
}

/// <summary>
/// Everything the window works with: the catalogue, the state of the computer, the staged changes and the
/// rows of the table, kept in step with one another.
/// </summary>
public sealed class Workspace
{
    public Workspace(CatalogStore catalog, IGpoStore store, IRegistryReader registry, AppPaths paths)
    {
        Catalog = catalog;
        Paths = paths;
        States = new PolicyStateService(catalog, store, registry);
        Pending = new PendingStore();
        Builder = new ChangeBuilder(States);
        Planner = new ChangePlanner(catalog, States);
        Backups = new BackupStore(paths.Backups);
        Applier = new ChangeApplier(States, Backups);
        Rows = catalog.Policies.Select(p => new PolicyRow(p)).ToList();
        Columns = new Columns(Rows);
        Engine = new FilterEngine(Columns);
        View = ViewState.Load(paths.View, Columns);

        States.Refreshed += (_, _) => UpdateRows();
        Pending.Changed += (_, _) => UpdateRows();
        UpdateRows();
    }

    public CatalogStore Catalog { get; }
    public AppPaths Paths { get; }
    public PolicyStateService States { get; }
    public PendingStore Pending { get; }
    public ChangeBuilder Builder { get; }
    public ChangePlanner Planner { get; }
    public BackupStore Backups { get; }
    public ChangeApplier Applier { get; }
    public IReadOnlyList<PolicyRow> Rows { get; }
    public Columns Columns { get; }
    public FilterEngine Engine { get; }
    public ViewState View { get; }

    /// <summary>The rows that pass the filters, in display order.</summary>
    public List<PolicyRow> Visible { get; private set; } = [];

    /// <summary>Raised after the state of rows changed, before the table is recomputed.</summary>
    public event EventHandler? RowsChanged;

    /// <summary>The website's summary line.</summary>
    public string MetaLine
    {
        get
        {
            var m = Catalog.Meta;
            var c = m.Counts;
            var generated = m.Generated.Length >= 10 ? m.Generated[..10] : m.Generated;
            return $"{c.Policies} policies · {c.WithMl} scored by {m.Model} · {c.InRtlfb} in RTLFB · "
                   + $"{c.InSb} in Security Baseline · generated {generated}";
        }
    }

    public List<PolicyRow> Compute() => Visible = Engine.Compute(Rows, View);

    /// <summary>Stages what a builder produced and withdraws what it found to be unnecessary.</summary>
    public void Stage(StageResult result)
    {
        Pending.RemoveMany(result.Withdrawn.Select(w => Pending.Get(w.Uid, w.Scope)).OfType<PendingChange>());
        Pending.SetMany(result.Staged);
    }

    public string DescribePending(PendingChange change)
    {
        var pol = Catalog[change.Uid];
        var summary = change.Target.Count == 0
            ? "Not configured"
            : States.Decoder.Decode(pol, change.Scope, change.Target)?.Summary ?? "Not configured";
        return (change.NeedsInput ? "needs input: " : "") + summary;
    }

    private void UpdateRows()
    {
        foreach (var row in Rows)
            row.Update(States.For(row.Def), Pending.For(row.Def).ToList(), DescribePending);
        RowsChanged?.Invoke(this, EventArgs.Empty);
    }
}
