using GpoCatalogue.Core.View;

namespace GpoCatalogue.App;

/// <summary>The row of filter controls under the column headers, kept aligned with the columns of the grid.</summary>
internal sealed class FilterBar : Panel
{
    private readonly CatalogGrid _grid;
    private readonly Workspace _ws;
    private readonly Dictionary<string, Control> _controls = [];
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 150 };
    private bool _syncing;

    public FilterBar(CatalogGrid grid, Workspace ws)
    {
        _grid = grid;
        _ws = ws;
        Height = LogicalToDeviceUnits(30);
        Dock = DockStyle.Top;
        Tag = "head";
        AccessibleName = "Filters";
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Changed?.Invoke(this, EventArgs.Empty);
        };

        foreach (DataGridViewColumn column in grid.Columns)
        {
            var control = Create(CatalogGrid.Def(column));
            control.Name = "filter_" + column.Name;
            if (control is TableLayoutPanel pair)
            {
                // min and max, or state and text
                pair.Controls[0].Name = control.Name + "_a";
                pair.Controls[1].Name = control.Name + "_b";
            }
            control.Visible = false;
            _controls[column.Name] = control;
            Controls.Add(control);
        }

        grid.Scroll += (_, _) => Align();
        grid.ColumnWidthChanged += (_, _) => Align();
        grid.ColumnDisplayIndexChanged += (_, _) => BeginInvoke(Align);
        grid.ColumnStateChanged += (_, _) => Align();
        grid.SizeChanged += (_, _) => Align();
        grid.RowsAdded += (_, _) => Align();
        grid.RowsRemoved += (_, _) => Align();
        SizeChanged += (_, _) => Align();
    }

    /// <summary>Raised after a filter changed; typing is debounced as on the website.</summary>
    public event EventHandler? Changed;

    public void Align()
    {
        var inset = LogicalToDeviceUnits(3);
        var frozen = _grid.Columns.GetColumnsWidth(DataGridViewElementStates.Visible | DataGridViewElementStates.Frozen);
        foreach (DataGridViewColumn column in _grid.Columns)
        {
            var control = _controls[column.Name];
            var rect = column.Visible ? _grid.GetColumnDisplayRectangle(column.Index, cutOverflow: false) : Rectangle.Empty;
            // A scrolling column that has slid under the frozen one is hidden rather than drawn over it.
            if (rect.Width <= 0 || (!column.Frozen && rect.Right <= frozen + inset * 4))
            {
                control.Visible = false;
                continue;
            }
            var left = column.Frozen ? rect.Left : Math.Max(rect.Left, frozen);
            control.SetBounds(left + inset, inset, rect.Right - left - inset * 2, Height - inset * 2);
            control.Visible = true;
            if (column.Frozen)
                control.BringToFront();
        }
    }

    /// <summary>Shows the filters of the view, for example after a preset or a reset.</summary>
    public void SyncFromView()
    {
        _syncing = true;
        try
        {
            foreach (var (id, control) in _controls)
            {
                var filter = _ws.View.Filters.GetValueOrDefault(id) ?? new FilterValue();
                switch (control)
                {
                    case TextBox box:
                        box.Text = filter.Text;
                        break;
                    case ComboBox combo:
                        combo.SelectedIndex = Math.Max(0, IndexOf(combo, filter.Mode));
                        break;
                    case TableLayoutPanel pair when pair.Controls[0] is TextBox min:
                        min.Text = filter.Min;
                        ((TextBox)pair.Controls[1]).Text = filter.Max;
                        break;
                    case TableLayoutPanel pair when pair.Controls[0] is ComboBox mode:
                        mode.SelectedIndex = Math.Max(0, IndexOf(mode, filter.Mode));
                        ((TextBox)pair.Controls[1]).Text = filter.Text;
                        break;
                }
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    public Control ControlFor(string column) => _controls[column];

    private Control Create(ColumnDef def)
    {
        var filter = () => _ws.View.Filter(def.Id);
        switch (def.Filter)
        {
            case FilterKind.Text:
                return TextFilter($"Filter {def.Label}", "contains…", text => filter().Text = text);
            case FilterKind.Range:
                var min = TextFilter($"{def.Label} minimum %", "min", text => filter().Min = text, lower: false);
                var max = TextFilter($"{def.Label} maximum %", "max", text => filter().Max = text, lower: false);
                return Pair(min, max, 50);
            case FilterKind.Baseline:
                var mode = Choice($"{def.Label} state", def.Options, value => filter().Mode = value);
                var text = TextFilter($"{def.Label} value contains", "value contains…", value => filter().Text = value);
                return Pair(mode, text, 50);
            case FilterKind.State:
                var state = Choice($"Filter {def.Label}", def.Options, value => filter().Mode = value);
                var contains = TextFilter($"{def.Label} contains", "contains…", value => filter().Text = value);
                return Pair(state, contains, 55);
            default:
                return Choice($"Filter {def.Label}", def.Options, value => filter().Mode = value);
        }
    }

    private TextBox TextFilter(string name, string placeholder, Action<string> set, bool lower = true)
    {
        var box = new TextBox { PlaceholderText = placeholder, AccessibleName = name, BorderStyle = BorderStyle.FixedSingle };
        box.TextChanged += (_, _) =>
        {
            if (_syncing)
                return;
            var text = box.Text.Trim();
            set(lower ? text.ToLowerInvariant() : text);
            _debounce.Stop();
            _debounce.Start();
        };
        return box;
    }

    private ComboBox Choice(string name, IReadOnlyList<FilterOption> options, Action<string> set)
    {
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = name, FlatStyle = FlatStyle.Flat };
        combo.Items.Add(new FilterOption("", "any"));
        foreach (var o in options)
            combo.Items.Add(o);
        combo.DisplayMember = nameof(FilterOption.Label);
        combo.SelectedIndex = 0;
        combo.DropDownWidth = Math.Max(combo.Width, options.Select(o => TextRenderer.MeasureText(o.Label, combo.Font).Width)
            .DefaultIfEmpty(0).Max() + LogicalToDeviceUnits(24));
        combo.SelectedIndexChanged += (_, _) =>
        {
            if (_syncing)
                return;
            set(((FilterOption)combo.SelectedItem!).Value);
            Changed?.Invoke(this, EventArgs.Empty);
        };
        return combo;
    }

    private static TableLayoutPanel Pair(Control left, Control right, int leftPercent)
    {
        var panel = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty, Tag = "head" };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, leftPercent));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100 - leftPercent));
        left.Dock = right.Dock = DockStyle.Fill;
        left.Margin = new Padding(0, 0, 2, 0);
        right.Margin = Padding.Empty;
        panel.Controls.Add(left, 0, 0);
        panel.Controls.Add(right, 1, 0);
        return panel;
    }

    private static int IndexOf(ComboBox combo, string value)
    {
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (((FilterOption)combo.Items[i]!).Value == value)
                return i;
        }
        return 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _debounce.Dispose();
        base.Dispose(disposing);
    }
}
