using System.Globalization;
using System.Text;
using GpoCatalogue.Core.Live;
using GpoCatalogue.Core.Model;

namespace GpoCatalogue.Core.View;

/// <summary>Filtering, sorting and export of the table, with the rules of the website (site/app.js).</summary>
public sealed class FilterEngine(Columns columns)
{
    // localeCompare(..., { numeric: true, sensitivity: 'base' }): digits by value, case and accents ignored.
    private static readonly CompareInfo Collation = CultureInfo.GetCultureInfo("en-US").CompareInfo;
    private const CompareOptions Natural =
        CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.NumericOrdering;

    public List<PolicyRow> Compute(IEnumerable<PolicyRow> rows, ViewState view)
    {
        // Filters of hidden columns apply as well.
        var active = columns.All
            .Select(c => (Column: c, Filter: view.Filters.GetValueOrDefault(c.Id)))
            .Where(f => f.Filter is { IsEmpty: false })
            .ToList();
        var visible = rows.Where(r =>
            (view.Search.Length == 0 || r.Search.Contains(view.Search, StringComparison.Ordinal))
            && active.All(f => Matches(r, f.Column, f.Filter!))).ToList();

        if (view.SortDirection != 0 && columns.Find(view.SortColumn) is { } column)
        {
            var direction = view.SortDirection;
            visible.Sort((a, b) =>
            {
                var order = Compare(a, b, column, direction);
                return order != 0 ? order : Collation.Compare(a.Uid, b.Uid, CompareOptions.None);
            });
        }
        return visible;
    }

    public static int Compare(PolicyRow a, PolicyRow b, ColumnDef column, int direction)
    {
        var va = column.Get(a);
        var vb = column.Get(b);
        if (column.Type == ColumnType.Percent)
        {
            // Rows without a score stay at the bottom whichever way the column is sorted.
            return (va, vb) switch
            {
                (null, null) => 0,
                (null, _) => 1,
                (_, null) => -1,
                _ => ((int)va).CompareTo((int)vb) * direction,
            };
        }
        return Collation.Compare(va?.ToString() ?? "", vb?.ToString() ?? "", Natural) * direction;
    }

    public static bool Matches(PolicyRow row, ColumnDef column, FilterValue filter)
    {
        var value = column.Get(row);
        switch (column.Filter)
        {
            case FilterKind.Text:
                return Contains(value, filter.Text);
            case FilterKind.Select:
                if (filter.Mode.Length == 0)
                    return true;
                // A policy with two sections matches when either of them does.
                if (column.Id == Columns.Live)
                    return row.States.Any(s => PolicyRow.LiveLabel(s.Live.Status) == filter.Mode);
                return (value?.ToString() ?? "") == filter.Mode;
            case FilterKind.Range:
                var number = (int?)value;
                // Comparisons with a bound that is not a number are false, so such a bound only drops unscored rows.
                if (filter.Min.Length > 0 && (number is null || number < Number(filter.Min)))
                    return false;
                if (filter.Max.Length > 0 && (number is null || number > Number(filter.Max)))
                    return false;
                return true;
            case FilterKind.Baseline:
                return MatchesBaseline(row, column, filter) && Contains(value, filter.Text);
            case FilterKind.State:
                return MatchesState(row, filter.Mode) && Contains(value, filter.Text);
            case FilterKind.Pending:
                return filter.Mode switch
                {
                    Columns.PendingAny => row.HasPending,
                    Columns.PendingNone => !row.HasPending,
                    Columns.PendingIncomplete => row.Pending.Any(p => p.NeedsInput),
                    _ => true,
                };
            default:
                return true;
        }
    }

    /// <summary>The rows as the website exports them: every visible column, then the explanation.</summary>
    public string ToCsv(IEnumerable<PolicyRow> rows, ViewState view)
    {
        var shown = columns.All.Where(c => !view.Hidden.Contains(c.Id)).ToList();
        var lines = new List<string>
        {
            string.Join(',', new[] { "uid" }.Concat(shown.Select(c => c.Label)).Append("Explanation").Select(CsvField)),
        };
        lines.AddRange(rows.Select(r => string.Join(',',
            new object?[] { r.Uid }.Concat(shown.Select(c => c.Get(r))).Append(r.Def.Explanation).Select(CsvField))));
        return string.Join("\r\n", lines);
    }

    public static void WriteCsv(string path, string csv) =>
        File.WriteAllText(path, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

    public static string CsvField(object? value)
    {
        var s = value switch
        {
            null => "",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };
        // Spreadsheets run cells that start like a formula.
        if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@')
            s = "'" + s;
        return s.AsSpan().IndexOfAny("\",\r\n") >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    private static bool Contains(object? value, string text) =>
        text.Length == 0 || (value?.ToString() ?? "").ToLowerInvariant().Contains(text, StringComparison.Ordinal);

    /// <summary>JavaScript's Number(text) for what a number box can hold.</summary>
    private static double Number(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : double.NaN;

    private static bool MatchesBaseline(PolicyRow row, ColumnDef column, FilterValue filter)
    {
        var applications = column.Id == Columns.Sb ? row.Def.Sb : row.Def.Rtlfb;
        var matches = column.Id == Columns.Sb ? row.MatchesSb : row.MatchesRtlfb;
        return filter.Mode switch
        {
            "" => true,
            Columns.ModeSet => applications.Count > 0,
            Columns.ModeUnset => applications.Count == 0,
            "Enabled" or "Disabled" => applications.Any(a => a.State == filter.Mode),
            Columns.ModeMatch => matches == true,
            Columns.ModeDiffer => matches == false,
            _ when filter.Mode.StartsWith(Columns.GpoPrefix, StringComparison.Ordinal) =>
                row.Def.Sb.Any(a => a.Gpo == filter.Mode[Columns.GpoPrefix.Length..]),
            _ => true,
        };
    }

    private static bool MatchesState(PolicyRow row, string mode) => mode switch
    {
        Columns.StateConfigured => row.IsConfigured,
        Columns.StateNotConfigured => !row.IsConfigured,
        Columns.StateEnabled => row.States.Any(s => s.State == PolicyState.Enabled),
        Columns.StateDisabled => row.States.Any(s => s.State == PolicyState.Disabled),
        Columns.StateRegistryOnly => row.States.Any(s => s.Live.Status == LiveStatus.RegistryOnly),
        Columns.StateNotApplied => row.States.Any(s => s.Live.Status == LiveStatus.NotApplied),
        _ => true,
    };
}
