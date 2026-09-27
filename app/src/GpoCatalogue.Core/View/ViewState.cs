using System.Text.Json;

namespace GpoCatalogue.Core.View;

/// <summary>What one column is filtered on. Which members matter depends on the kind of filter.</summary>
public sealed class FilterValue
{
    /// <summary>Text to contain, lowercased; for baseline filters the text its summary must contain.</summary>
    public string Text { get; set; } = "";

    /// <summary>The chosen option of a list; empty for "any".</summary>
    public string Mode { get; set; } = "";

    public string Min { get; set; } = "";
    public string Max { get; set; } = "";

    public bool IsEmpty => Text.Length == 0 && Mode.Length == 0 && Min.Length == 0 && Max.Length == 0;

    public FilterValue Clone() => (FilterValue)MemberwiseClone();
}

/// <summary>Everything that decides which rows are shown and how: the counterpart of the website's address.</summary>
public sealed class ViewState
{
    public string Search { get; set; } = "";
    public Dictionary<string, FilterValue> Filters { get; set; } = [];
    public string SortColumn { get; set; } = "";

    /// <summary>1 ascending, -1 descending, 0 unsorted.</summary>
    public int SortDirection { get; set; }

    public HashSet<string> Hidden { get; set; } = [];

    // Layout, which the website has no counterpart for.
    public Dictionary<string, int> Widths { get; set; } = [];
    public List<string> Order { get; set; } = [];
    public int DetailHeight { get; set; }
    public bool DetailCollapsed { get; set; }
    public int[]? Window { get; set; }
    public bool Maximized { get; set; }

    public FilterValue Filter(string column)
    {
        if (!Filters.TryGetValue(column, out var value))
            Filters[column] = value = new FilterValue();
        return value;
    }

    public bool HasFilters => Search.Length > 0 || Filters.Values.Any(f => !f.IsEmpty);

    public void ClearFilters()
    {
        Filters.Clear();
        Search = "";
    }

    /// <summary>Back to what a first start shows. The window itself stays where it is.</summary>
    public void Reset(Columns columns)
    {
        ClearFilters();
        SortColumn = "";
        SortDirection = 0;
        Hidden = [.. columns.DefaultHidden];
        Widths.Clear();
        Order.Clear();
    }

    /// <summary>Percent columns start with the strongest answers on top; every further click reverses the order.</summary>
    public void ToggleSort(ColumnDef column)
    {
        if (SortColumn != column.Id || SortDirection == 0)
        {
            SortColumn = column.Id;
            SortDirection = column.Type == ColumnType.Percent ? -1 : 1;
        }
        else
        {
            SortDirection = -SortDirection;
        }
    }

    public static ViewState Load(string path, Columns columns)
    {
        ViewState? state = null;
        try
        {
            if (File.Exists(path))
                state = JsonSerializer.Deserialize<ViewState>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A view that cannot be read is no reason not to start.
        }
        if (state is null)
        {
            state = new ViewState();
            state.Reset(columns);
            return state;
        }
        // The file may come from a version with other columns.
        state.Hidden.RemoveWhere(id => columns.Find(id) is null);
        foreach (var id in state.Filters.Keys.Where(id => columns.Find(id) is null).ToList())
            state.Filters.Remove(id);
        if (columns.Find(state.SortColumn) is null)
        {
            state.SortColumn = "";
            state.SortDirection = 0;
        }
        return state;
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the layout is not worth an error message on the way out.
        }
    }
}

/// <summary>The website's quick presets (site/app.js applyPreset).</summary>
public sealed record Preset(string Id, string Label)
{
    public static readonly IReadOnlyList<Preset> All =
    [
        new("", "none"),
        new("privacy", "Privacy ≥ 50%, UX loss < 50%"),
        new("privacy_unbaselined", "Privacy ≥ 50%, not in RTLFB"),
        new("security", "Security ≥ 50%, UX loss < 50%"),
        new("rtlfb", "In RTLFB"),
        new("sb", "In Security Baseline"),
        new("both", "In both baselines"),
    ];

    /// <summary>Replaces all filters and the search; the sort order changes only for the ranking presets.</summary>
    public void Apply(ViewState view)
    {
        view.ClearFilters();
        switch (Id)
        {
            case "privacy":
                view.Filter(Columns.Privacy).Min = "50";
                view.Filter(Columns.Ux).Max = "49";
                (view.SortColumn, view.SortDirection) = (Columns.Privacy, -1);
                break;
            case "privacy_unbaselined":
                view.Filter(Columns.Privacy).Min = "50";
                view.Filter(Columns.Rtlfb).Mode = Columns.ModeUnset;
                (view.SortColumn, view.SortDirection) = (Columns.Privacy, -1);
                break;
            case "security":
                view.Filter(Columns.Security).Min = "50";
                view.Filter(Columns.Ux).Max = "49";
                (view.SortColumn, view.SortDirection) = (Columns.Security, -1);
                break;
            case "rtlfb":
                view.Filter(Columns.Rtlfb).Mode = Columns.ModeSet;
                break;
            case "sb":
                view.Filter(Columns.Sb).Mode = Columns.ModeSet;
                break;
            case "both":
                view.Filter(Columns.Rtlfb).Mode = Columns.ModeSet;
                view.Filter(Columns.Sb).Mode = Columns.ModeSet;
                break;
        }
    }
}
