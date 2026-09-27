using System.Text.Json;
using GpoCatalogue.Core.View;

namespace GpoCatalogue.Tests;

/// <summary>
/// Holds filtering, sorting and export to what the website itself produces. The reference file is recorded
/// from the real site by tools/site-golden.js.
/// </summary>
public class ViewParityTests : IDisposable
{
    private static readonly Lazy<JsonDocument> Golden = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(Repo.Fixture("site-golden.json"))));

    private readonly TestWorkspace _test = new();

    public static TheoryData<string> ViewNames() =>
        new(Golden.Value.RootElement.GetProperty("views").EnumerateArray().Select(v => v.GetProperty("name").GetString()!));

    public void Dispose() => _test.Dispose();

    [Fact]
    public void Site_columns_keep_their_label_order_and_visibility()
    {
        var site = Golden.Value.RootElement.GetProperty("columns").EnumerateArray()
            .Select(c => (c.GetProperty("id").GetString()!, c.GetProperty("label").GetString()!, c.GetProperty("hidden").GetBoolean()))
            .ToList();
        var ids = site.Select(c => c.Item1).ToHashSet();

        var mine = _test.Workspace.Columns.All.Where(c => ids.Contains(c.Id))
            .Select(c => (c.Id, c.Label, c.Hidden)).ToList();

        Assert.Equal(site, mine);
    }

    [Theory]
    [MemberData(nameof(ViewNames))]
    public void View_shows_the_rows_the_site_shows(string name)
    {
        var golden = Golden.Value.RootElement.GetProperty("views").EnumerateArray()
            .Single(v => v.GetProperty("name").GetString() == name);
        var ws = _test.Workspace;
        var view = ws.View;
        view.Reset(ws.Columns);
        // Columns the site does not have stay out of the export.
        view.Hidden.UnionWith([Columns.State, Columns.Pending, Columns.Live]);
        view.Search = golden.GetProperty("search").GetString()!;
        foreach (var f in golden.GetProperty("filters").EnumerateObject())
        {
            var filter = view.Filter(f.Name);
            filter.Text = Text(f.Value, "text");
            filter.Mode = Text(f.Value, "mode");
            filter.Min = Text(f.Value, "min");
            filter.Max = Text(f.Value, "max");
        }
        if (golden.GetProperty("sort") is { ValueKind: JsonValueKind.Object } sort)
        {
            view.SortColumn = sort.GetProperty("column").GetString()!;
            view.SortDirection = sort.GetProperty("direction").GetInt32();
        }

        var rows = ws.Compute();

        var head = Strings(golden.GetProperty("head"));
        var tail = Strings(golden.GetProperty("tail"));
        Assert.Equal(golden.GetProperty("count").GetInt32(), rows.Count);
        Assert.Equal(head, rows.Take(head.Count).Select(r => r.Uid));
        Assert.Equal(tail, rows.Skip(Math.Max(0, rows.Count - tail.Count)).Select(r => r.Uid));
        if (golden.GetProperty("csv").GetString() is { } csv)
            Assert.Equal(csv, ws.Engine.ToCsv(rows, view));
    }

    [Fact]
    public void Presets_are_the_sites()
    {
        var ws = _test.Workspace;
        ws.View.Filter(Columns.Name).Text = "x";
        ws.View.Search = "y";
        (ws.View.SortColumn, ws.View.SortDirection) = (Columns.Name, 1);

        Preset.All.Single(p => p.Id == "rtlfb").Apply(ws.View);

        // A preset replaces every filter and the search, and leaves the sort order unless it ranks.
        Assert.Equal("", ws.View.Search);
        Assert.True(ws.View.Filter(Columns.Name).IsEmpty);
        Assert.Equal((Columns.Name, 1), (ws.View.SortColumn, ws.View.SortDirection));
        Assert.Equal(109, ws.Compute().Count);

        Preset.All.Single(p => p.Id == "privacy").Apply(ws.View);
        Assert.Equal((Columns.Privacy, -1), (ws.View.SortColumn, ws.View.SortDirection));
        Assert.Equal(7, Preset.All.Count);
    }

    [Fact]
    public void Csv_fields_are_escaped_like_the_sites()
    {
        foreach (var pair in Golden.Value.RootElement.GetProperty("csvFields").EnumerateArray())
            Assert.Equal(pair[1].GetString(), FilterEngine.CsvField(pair[0].GetString()));
    }

    [Fact]
    public void First_click_sorts_percentages_descending_and_text_ascending()
    {
        var ws = _test.Workspace;
        ws.View.Reset(ws.Columns);

        ws.View.ToggleSort(ws.Columns[Columns.Privacy]);
        Assert.Equal(-1, ws.View.SortDirection);
        ws.View.ToggleSort(ws.Columns[Columns.Privacy]);
        Assert.Equal(1, ws.View.SortDirection);
        ws.View.ToggleSort(ws.Columns[Columns.Name]);
        Assert.Equal((Columns.Name, 1), (ws.View.SortColumn, ws.View.SortDirection));
    }

    [Fact]
    public void Summary_line_reads_like_the_sites()
    {
        var meta = Repo.SiteData.GetProperty("meta");
        var counts = meta.GetProperty("counts");
        var expected = $"{counts.GetProperty("policies")} policies · {counts.GetProperty("with_ml")} scored by "
                       + $"{meta.GetProperty("model").GetString()} · {counts.GetProperty("in_rtlfb")} in RTLFB · "
                       + $"{counts.GetProperty("in_sb")} in Security Baseline · generated "
                       + meta.GetProperty("generated").GetString()![..10];

        Assert.Equal(expected, _test.Workspace.MetaLine);
    }

    [Fact]
    public void View_survives_saving_and_loading()
    {
        var ws = _test.Workspace;
        ws.View.Reset(ws.Columns);
        ws.View.Search = "cortana";
        ws.View.Filter(Columns.Privacy).Min = "50";
        ws.View.Filter(Columns.Sb).Mode = Columns.ModeSet;
        ws.View.Hidden.Add(Columns.Category);
        (ws.View.SortColumn, ws.View.SortDirection) = (Columns.Security, -1);
        var path = Path.Combine(_test.Folder, "view.json");

        ws.View.Save(path);
        var loaded = ViewState.Load(path, ws.Columns);

        Assert.Equal("cortana", loaded.Search);
        Assert.Equal("50", loaded.Filter(Columns.Privacy).Min);
        Assert.Equal(Columns.ModeSet, loaded.Filter(Columns.Sb).Mode);
        Assert.Contains(Columns.Category, loaded.Hidden);
        Assert.Equal((Columns.Security, -1), (loaded.SortColumn, loaded.SortDirection));
    }

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";

    private static List<string> Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()!).ToList();
}
