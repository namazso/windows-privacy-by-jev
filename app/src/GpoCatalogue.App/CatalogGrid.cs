using System.Drawing.Drawing2D;
using GpoCatalogue.Core.Live;
using GpoCatalogue.Core.Model;
using GpoCatalogue.Core.State;
using GpoCatalogue.Core.View;

namespace GpoCatalogue.App;

/// <summary>The policy table. Rows come from the workspace on demand; every cell is painted here.</summary>
internal sealed class CatalogGrid : DataGridView
{
    private const int PercentLow = 35, PercentHigh = 65;
    private const TextFormatFlags Wrapped = TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis
        | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix;
    private const TextFormatFlags OneLine = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis
        | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter;

    private readonly Workspace _ws;

    public CatalogGrid(Workspace ws)
    {
        _ws = ws;
        DoubleBuffered = true;
        VirtualMode = true;
        ReadOnly = true;
        AllowUserToAddRows = false;
        AllowUserToDeleteRows = false;
        AllowUserToResizeRows = false;
        AllowUserToOrderColumns = true;
        RowHeadersVisible = false;
        SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        MultiSelect = true;
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        EnableHeadersVisualStyles = false;
        BorderStyle = BorderStyle.None;
        CellBorderStyle = DataGridViewCellBorderStyle.None;
        ShowCellToolTips = true;
        StandardTab = true;
        AccessibleName = "Policies";

        foreach (var def in ws.Columns.All)
        {
            Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = def.Id,
                HeaderText = def.Label,
                ToolTipText = def.Tooltip,
                SortMode = DataGridViewColumnSortMode.Programmatic,
                Width = LogicalToDeviceUnits(ws.View.Widths.GetValueOrDefault(def.Id, def.Width)),
                MinimumWidth = LogicalToDeviceUnits(40),
                Visible = !ws.View.Hidden.Contains(def.Id),
                Tag = def,
            });
        }
        for (var i = 0; i < ws.View.Order.Count; i++)
        {
            if (Columns[ws.View.Order[i]] is { } column)
                column.DisplayIndex = Math.Min(i, Columns.Count - 1);
        }
        Columns[Core.View.Columns.Name]!.DisplayIndex = 0;
        Columns[Core.View.Columns.Name]!.Frozen = true;
        ApplyTheme();
    }

    /// <summary>Raised when a header is clicked to sort.</summary>
    public event EventHandler<ColumnDef>? SortRequested;

    public PolicyRow? CurrentPolicy => CurrentCell is { RowIndex: var i } && i >= 0 && i < _ws.Visible.Count
        ? _ws.Visible[i] : null;

    public static ColumnDef Def(DataGridViewColumn column) => (ColumnDef)column.Tag!;

    public void ApplyTheme()
    {
        var t = Theme.Current;
        BackgroundColor = t.Bg;
        GridColor = t.Line;
        DefaultCellStyle.BackColor = t.Bg;
        DefaultCellStyle.ForeColor = t.Fg;
        DefaultCellStyle.SelectionBackColor = t.Selection;
        DefaultCellStyle.SelectionForeColor = t.Fg;
        DefaultCellStyle.Font = t.Body;
        ColumnHeadersDefaultCellStyle.BackColor = t.Head;
        ColumnHeadersDefaultCellStyle.ForeColor = t.Fg;
        ColumnHeadersDefaultCellStyle.SelectionBackColor = t.Head;
        ColumnHeadersDefaultCellStyle.Font = t.Bold;
        ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
        ColumnHeadersHeight = LogicalToDeviceUnits(30);
        RowTemplate.Height = t.Body.Height * 2 + LogicalToDeviceUnits(10);
    }

    /// <summary>Shows the rows of the workspace again, keeping the selection where it can.</summary>
    public void Reload()
    {
        var selected = SelectedPolicies().Select(r => r.Uid).ToHashSet();
        var current = CurrentPolicy?.Uid;
        var firstShown = FirstDisplayedScrollingRowIndex >= 0 && FirstDisplayedScrollingRowIndex < RowCount
            ? _ws.Visible.ElementAtOrDefault(FirstDisplayedScrollingRowIndex)?.Uid : null;

        RowCount = 0;
        RowCount = _ws.Visible.Count;
        UpdateSortGlyphs();
        ClearSelection();
        if (_ws.Visible.Count == 0)
            return;

        var index = _ws.Visible.FindIndex(r => r.Uid == current);
        if (index >= 0)
        {
            CurrentCell = this[FirstVisibleColumn(), index];
            var top = firstShown is null ? -1 : _ws.Visible.FindIndex(r => r.Uid == firstShown);
            if (top >= 0)
                FirstDisplayedScrollingRowIndex = top;
        }
        else
        {
            CurrentCell = null;
        }
        ClearSelection();
        for (var i = 0; i < _ws.Visible.Count && selected.Count > 0; i++)
        {
            if (selected.Contains(_ws.Visible[i].Uid))
                Rows[i].Selected = true;
        }
    }

    /// <summary>Repaints after the state of rows changed without the set of rows changing.</summary>
    public void RefreshCells() => Invalidate();

    public void Select(string uid)
    {
        var index = _ws.Visible.FindIndex(r => r.Uid == uid);
        if (index < 0)
            return;
        ClearSelection();
        CurrentCell = this[FirstVisibleColumn(), index];
        Rows[index].Selected = true;
    }

    public List<PolicyRow> SelectedPolicies()
    {
        var rows = new List<PolicyRow>();
        for (var i = 0; i < RowCount && i < _ws.Visible.Count; i++)
        {
            if ((Rows.GetRowState(i) & DataGridViewElementStates.Selected) != 0)
                rows.Add(_ws.Visible[i]);
        }
        return rows;
    }

    public void UpdateSortGlyphs()
    {
        foreach (DataGridViewColumn c in Columns)
        {
            c.HeaderCell.SortGlyphDirection = c.Name == _ws.View.SortColumn && _ws.View.SortDirection != 0
                ? (_ws.View.SortDirection > 0 ? SortOrder.Ascending : SortOrder.Descending)
                : SortOrder.None;
        }
    }

    /// <summary>Remembers widths and order in the view.</summary>
    public void SaveLayout()
    {
        foreach (DataGridViewColumn c in Columns)
            _ws.View.Widths[c.Name] = DeviceToLogicalUnits(c.Width);
        _ws.View.Order = Columns.Cast<DataGridViewColumn>().OrderBy(c => c.DisplayIndex).Select(c => c.Name).ToList();
    }

    private int DeviceToLogicalUnits(int value) => (int)Math.Round(value * 96.0 / DeviceDpi);

    private int FirstVisibleColumn() => Columns.GetFirstColumn(DataGridViewElementStates.Visible)?.Index ?? 0;

    protected override void OnColumnHeaderMouseClick(DataGridViewCellMouseEventArgs e)
    {
        base.OnColumnHeaderMouseClick(e);
        if (e.Button == MouseButtons.Left)
            SortRequested?.Invoke(this, Def(Columns[e.ColumnIndex]));
    }

    protected override void OnCellValueNeeded(DataGridViewCellValueEventArgs e)
    {
        base.OnCellValueNeeded(e);
        if (e.RowIndex >= _ws.Visible.Count)
            return;
        var def = Def(Columns[e.ColumnIndex]);
        var value = def.Get(_ws.Visible[e.RowIndex]);
        e.Value = def.Type == ColumnType.Percent ? value is null ? "–" : value + "%" : value?.ToString() ?? "";
    }

    protected override void OnCellToolTipTextNeeded(DataGridViewCellToolTipTextNeededEventArgs e)
    {
        base.OnCellToolTipTextNeeded(e);
        if (e.RowIndex < 0 || e.RowIndex >= _ws.Visible.Count || e.ColumnIndex < 0)
            return;
        var row = _ws.Visible[e.RowIndex];
        var def = Def(Columns[e.ColumnIndex]);
        e.ToolTipText = def.Id switch
        {
            Core.View.Columns.PrivacyDir when row.Def.Ml is { } ml =>
                $"Model choice: {ml.PrivacyDir} (confidence {PolicyRow.Percent(ml.PrivacyDirConf)}%)",
            Core.View.Columns.SecurityDir when row.Def.Ml is { } ml =>
                $"Model choice: {ml.SecurityDir} (confidence {PolicyRow.Percent(ml.SecurityDirConf)}%)",
            Core.View.Columns.Name => row.Def.Name + string.Concat(row.Def.Flags.Select(f => "\nFlag: " + f)),
            Core.View.Columns.Rtlfb or Core.View.Columns.Sb => string.Join("\n", Applications(row, def.Id)
                .Select(a => a.Summary + "  (" + Sub(row, def.Id, a) + ")")),
            Core.View.Columns.State => string.Join("\n", row.States.Select(s =>
                $"{s.Scope.Display()}: {s.Text}" + (s.Live.Status == LiveStatus.None ? "" : $"  [{PolicyRow.LiveLabel(s.Live.Status)}]"))),
            Core.View.Columns.Pending => row.PendingText.Replace(" | ", "\n"),
            _ => def.Get(row)?.ToString() ?? "",
        };
    }

    protected override void OnCellPainting(DataGridViewCellPaintingEventArgs e)
    {
        base.OnCellPainting(e);
        if (e.Graphics is not { } g)
            return;
        var t = Theme.Current;
        if (e.RowIndex < 0)
        {
            if (e.ColumnIndex >= 0)
                PaintHeader(e, g, t);
            return;
        }
        if (e.ColumnIndex < 0 || e.RowIndex >= _ws.Visible.Count)
            return;

        var row = _ws.Visible[e.RowIndex];
        var def = Def(Columns[e.ColumnIndex]);
        var selected = (e.State & DataGridViewElementStates.Selected) != 0;
        var back = selected ? t.Selection
            : def.Id == Core.View.Columns.Pending && row.HasPending ? t.MidBg
            : e.RowIndex % 2 == 1 ? t.RowAlt : t.Bg;
        using (var brush = new SolidBrush(back))
            g.FillRectangle(brush, e.CellBounds);
        using (var pen = new Pen(t.Line))
            g.DrawLine(pen, e.CellBounds.Left, e.CellBounds.Bottom - 1, e.CellBounds.Right, e.CellBounds.Bottom - 1);

        var pad = LogicalToDeviceUnits(6);
        var box = Rectangle.FromLTRB(e.CellBounds.Left + pad, e.CellBounds.Top + LogicalToDeviceUnits(4),
            e.CellBounds.Right - pad, e.CellBounds.Bottom - LogicalToDeviceUnits(5));
        var line = t.Body.Height;
        var first = box with { Height = line };
        var second = new Rectangle(box.Left, box.Top + line, box.Width, box.Height - line);

        switch (def.Id)
        {
            case Core.View.Columns.Privacy or Core.View.Columns.Security or Core.View.Columns.Ux:
                PaintPercent(g, t, first, (int?)def.Get(row));
                break;
            case Core.View.Columns.Name:
                if (row.HasPending)
                {
                    using var accent = new SolidBrush(t.Accent);
                    g.FillRectangle(accent, e.CellBounds.Left, e.CellBounds.Top, LogicalToDeviceUnits(3), e.CellBounds.Height - 1);
                }
                var chips = PaintFlags(g, t, first, row.Def.Flags);
                TextRenderer.DrawText(g, row.Def.Name, t.Body, box with { Width = Math.Max(0, box.Width - chips) },
                    t.Fg, Wrapped);
                break;
            case Core.View.Columns.Rtlfb or Core.View.Columns.Sb:
                var apps = Applications(row, def.Id);
                if (apps.Count == 0)
                    break;
                TextRenderer.DrawText(g, apps[0].Summary, t.Body, first, t.Fg, OneLine);
                var more = apps.Count > 1 ? $" · +{apps.Count - 1} more" : "";
                TextRenderer.DrawText(g, Sub(row, def.Id, apps[0]) + more, t.Small, second with { Height = t.Small.Height + 2 },
                    t.Muted, OneLine);
                break;
            case Core.View.Columns.State:
                PaintState(g, t, row, first, second);
                break;
            case Core.View.Columns.Pending:
                var lines = row.Pending.ToList();
                for (var i = 0; i < lines.Count && i < 2; i++)
                {
                    var p = lines[i];
                    var text = (row.Def.Scopes.Count > 1 ? p.Scope.Display() + ": " : "") + _ws.DescribePending(p);
                    if (i == 1 && lines.Count > 2)
                        text = $"+{lines.Count - 1} more";
                    TextRenderer.DrawText(g, text, t.Body, i == 0 ? first : second with { Height = line },
                        p.NeedsInput ? t.FlagFg : t.MidFg, OneLine);
                }
                break;
            default:
                var font = def.Id is Core.View.Columns.Admx or Core.View.Columns.ValueName or Core.View.Columns.RegKey
                    ? t.Mono : t.Body;
                var color = def.Id is Core.View.Columns.Category or Core.View.Columns.Supported ? t.Muted
                    : def.Id is Core.View.Columns.Live ? t.Muted : t.Fg;
                TextRenderer.DrawText(g, def.Get(row)?.ToString() ?? "", font, box, color, Wrapped);
                break;
        }
        e.Handled = true;
    }

    private void PaintHeader(DataGridViewCellPaintingEventArgs e, Graphics g, Theme t)
    {
        using (var brush = new SolidBrush(t.Head))
            g.FillRectangle(brush, e.CellBounds);
        using (var pen = new Pen(t.Line))
        {
            g.DrawLine(pen, e.CellBounds.Left, e.CellBounds.Bottom - 1, e.CellBounds.Right, e.CellBounds.Bottom - 1);
            g.DrawLine(pen, e.CellBounds.Right - 1, e.CellBounds.Top + 6, e.CellBounds.Right - 1, e.CellBounds.Bottom - 6);
        }
        var column = Columns[e.ColumnIndex];
        var sorted = column.HeaderCell.SortGlyphDirection;
        var arrow = sorted == SortOrder.Ascending ? " ▲" : sorted == SortOrder.Descending ? " ▼" : "";
        var pad = LogicalToDeviceUnits(6);
        var box = Rectangle.FromLTRB(e.CellBounds.Left + pad, e.CellBounds.Top, e.CellBounds.Right - pad, e.CellBounds.Bottom);
        TextRenderer.DrawText(g, column.HeaderText + arrow, t.Bold, box, sorted == SortOrder.None ? t.Fg : t.Accent, OneLine);
        e.Handled = true;
    }

    private void PaintPercent(Graphics g, Theme t, Rectangle line, int? value)
    {
        if (value is null)
        {
            TextRenderer.DrawText(g, "–", t.Body, line, t.Muted, OneLine | TextFormatFlags.Right);
            return;
        }
        var (bg, fg) = value < PercentLow ? (t.LowBg, t.LowFg) : value > PercentHigh ? (t.HighBg, t.HighFg) : (t.MidBg, t.MidFg);
        var text = value + "%";
        var size = TextRenderer.MeasureText(g, text, t.Body, Size.Empty, TextFormatFlags.NoPadding);
        var width = Math.Max(size.Width + LogicalToDeviceUnits(14), LogicalToDeviceUnits(46));
        var pill = new Rectangle(line.Right - width, line.Top - 1, width, line.Height + 2);
        FillRounded(g, pill, pill.Height / 2, bg);
        TextRenderer.DrawText(g, text, t.Body, pill with { Width = pill.Width - LogicalToDeviceUnits(7) }, fg,
            OneLine | TextFormatFlags.Right);
    }

    /// <summary>Draws flag chips at the right end of the first line and returns the width they take.</summary>
    private int PaintFlags(Graphics g, Theme t, Rectangle line, IReadOnlyList<string> flags)
    {
        var x = line.Right;
        foreach (var flag in flags.Reverse())
        {
            var text = flag.Replace('_', ' ');
            var size = TextRenderer.MeasureText(g, text, t.Small, Size.Empty, TextFormatFlags.NoPadding);
            var chip = new Rectangle(x - size.Width - LogicalToDeviceUnits(10), line.Top + 1, size.Width + LogicalToDeviceUnits(10), line.Height - 1);
            FillRounded(g, chip, LogicalToDeviceUnits(3), t.FlagBg);
            TextRenderer.DrawText(g, text, t.Small, chip, t.FlagFg, OneLine | TextFormatFlags.HorizontalCenter);
            x = chip.Left - LogicalToDeviceUnits(4);
        }
        return line.Right - x;
    }

    private static void PaintState(Graphics g, Theme t, PolicyRow row, Rectangle first, Rectangle second)
    {
        var both = row.StateText.Contains(" | ", StringComparison.Ordinal);
        var lines = both
            ? row.States.Select(s => (Text: $"{s.Scope.Display()}: {s.Text}", State: s)).ToList()
            : row.States.Take(1).Select(s => (Text: s.Text, State: s)).ToList();
        for (var i = 0; i < lines.Count && i < 2; i++)
        {
            var s = lines[i].State;
            var color = s.Live.Status switch
            {
                LiveStatus.NotApplied => t.FlagFg,
                LiveStatus.RegistryOnly => t.MidFg,
                _ when s.IsConfigured => t.Fg,
                _ => t.Muted,
            };
            var font = s.IsConfigured ? t.Bold : t.Body;
            TextRenderer.DrawText(g, lines[i].Text, font, i == 0 ? first : second with { Height = first.Height }, color, OneLine);
        }
    }

    private static List<BaselineApplication> Applications(PolicyRow row, string column) =>
        column == Core.View.Columns.Sb ? row.Def.Sb : row.Def.Rtlfb;

    private static string Sub(PolicyRow row, string column, BaselineApplication a)
    {
        var scope = a.Scope == "Machine" ? "Computer" : "User";
        return column == Core.View.Columns.Sb ? PolicyRow.StripMsft(a.Gpo) + " · " + scope : scope;
    }

    private static void FillRounded(Graphics g, Rectangle r, int radius, Color color)
    {
        if (r.Width <= 0 || r.Height <= 0)
            return;
        radius = Math.Max(1, Math.Min(radius, Math.Min(r.Width, r.Height) / 2));
        using var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        var mode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var brush = new SolidBrush(color))
            g.FillPath(brush, path);
        g.SmoothingMode = mode;
    }
}
