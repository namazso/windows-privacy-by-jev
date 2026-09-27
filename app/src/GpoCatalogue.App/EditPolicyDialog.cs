using GpoCatalogue.Core.Changes;
using GpoCatalogue.Core.Encode;
using GpoCatalogue.Core.Model;
using GpoCatalogue.Core.View;

namespace GpoCatalogue.App;

/// <summary>Sets one policy the way the Group Policy editor does: a state and, when enabled, its options.</summary>
internal sealed class EditPolicyDialog : Form
{
    private readonly Workspace _ws;
    private readonly PolicyDef _pol;
    private readonly RadioButton _notConfigured = new() { Name = "notConfigured", Text = "&Not configured", AutoSize = true };
    private readonly RadioButton _enabled = new() { Name = "enabled", Text = "&Enabled", AutoSize = true };
    private readonly RadioButton _disabled = new() { Name = "disabled", Text = "&Disabled", AutoSize = true };
    private readonly ComboBox _scope = new() { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Section" };
    private readonly Label _current = new() { Name = "current", AutoSize = true };
    private readonly FlowLayoutPanel _options = new()
    {
        FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Dock = DockStyle.Fill,
        AccessibleName = "Options",
    };
    private readonly Label _problems = new() { Name = "problems", AutoSize = false, Dock = DockStyle.Fill };
    private readonly Dictionary<string, Editor> _editors = [];
    private Scope _loadedScope;

    public EditPolicyDialog(Workspace ws, PolicyDef pol, Scope scope)
    {
        _ws = ws;
        _pol = pol;
        Text = "Edit policy – " + pol.Name;
        AccessibleName = "Edit policy";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Font = Theme.Current.Body;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(LogicalToDeviceUnits(900), LogicalToDeviceUnits(620));
        MinimumSize = new Size(LogicalToDeviceUnits(640), LogicalToDeviceUnits(420));

        var title = new Label { Text = pol.Name, Font = Theme.Current.Heading, AutoSize = true, MaximumSize = new Size(LogicalToDeviceUnits(860), 0) };
        var states = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
        states.Controls.AddRange([_notConfigured, _enabled, _disabled]);
        if (pol.Scopes.Count > 1)
        {
            states.Controls.Add(new Label { Text = "Section:", AutoSize = true, Margin = new Padding(24, 6, 4, 0) });
            _scope.Items.AddRange([Scope.Machine.Display(), Scope.User.Display()]);
            _scope.Width = LogicalToDeviceUnits(110);
            states.Controls.Add(_scope);
        }
        _current.Margin = new Padding(0, 4, 0, 4);

        var help = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
            Text = (pol.SupportedOn.Length > 0 ? "Supported on: " + pol.SupportedOn + "\r\n\r\n" : "")
                   + pol.Explanation.Replace("\n", "\r\n"),
            AccessibleName = "Help",
        };
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };
        split.Panel1.Controls.Add(_options);
        split.Panel1.Controls.Add(new Label { Text = "Options:", Dock = DockStyle.Top, Height = LogicalToDeviceUnits(22) });
        split.Panel2.Controls.Add(help);
        split.Panel2.Controls.Add(new Label { Text = "Help:", Dock = DockStyle.Top, Height = LogicalToDeviceUnits(22) });

        var ok = new Button { Name = "stage", Text = "&Stage change", AutoSize = true, DialogResult = DialogResult.None };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => Accept();
        AcceptButton = ok;
        CancelButton = cancel;
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
        buttons.Controls.AddRange([cancel, ok]);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(LogicalToDeviceUnits(10)) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(title, 0, 0);
        layout.SetColumnSpan(title, 2);
        layout.Controls.Add(states, 0, 1);
        layout.SetColumnSpan(states, 2);
        layout.Controls.Add(_current, 0, 2);
        layout.SetColumnSpan(_current, 2);
        layout.Controls.Add(split, 0, 3);
        layout.SetColumnSpan(split, 2);
        layout.Controls.Add(_problems, 0, 4);
        layout.Controls.Add(buttons, 1, 4);
        Controls.Add(layout);

        BuildEditors();
        foreach (var radio in new[] { _notConfigured, _enabled, _disabled })
            radio.CheckedChanged += (_, _) => UpdateEnabled();
        _scope.SelectedIndexChanged += (_, _) => LoadScope(_scope.SelectedIndex == 1 ? Scope.User : Scope.Machine);
        _options.SizeChanged += (_, _) => FitEditors();
        Theme.Current.Apply(this);
        help.BackColor = Theme.Current.Head;
        _problems.ForeColor = Theme.Current.FlagFg;

        if (pol.Scopes.Count > 1)
            _scope.SelectedIndex = scope == Scope.User ? 1 : 0; // loads
        else
            LoadScope(scope);
        Shown += (_, _) =>
        {
            split.SplitterDistance = (int)(split.Width * 0.55);
            FitEditors();
        };
    }

    /// <summary>What was staged when the dialog was accepted.</summary>
    public StageResult? Result { get; private set; }

    private void LoadScope(Scope scope)
    {
        _loadedScope = scope;
        var decoder = _ws.States.Decoder;
        var current = _ws.States[_pol.Uid, scope];
        var pending = _ws.Pending.Get(_pol.Uid, scope);
        var entries = pending?.Target ?? current.Entries;
        var state = pending is null ? current.State
            : pending.Target.Count == 0 ? PolicyState.NotConfigured
            : decoder.Decode(_pol, scope, pending.Target)?.State ?? PolicyState.NotConfigured;
        if (pending?.Intended is { } intended)
            state = intended;

        var options = OptionReader.Defaults(_pol);
        foreach (var (id, value) in OptionReader.Read(_pol, decoder.Pair(_pol, scope, entries)))
            options[id] = value;
        foreach (var (id, editor) in _editors)
            editor.Set(options.GetValueOrDefault(id));

        _notConfigured.Checked = state == PolicyState.NotConfigured;
        _enabled.Checked = state == PolicyState.Enabled;
        _disabled.Checked = state == PolicyState.Disabled;
        _current.Text = "Currently: " + current.Text + (pending is null ? "" : "      Staged: " + _ws.DescribePending(pending));
        _problems.Text = "";
        UpdateEnabled();
    }

    private PolicyConfig Build()
    {
        if (_notConfigured.Checked)
            return PolicyConfig.NotConfigured;
        if (_disabled.Checked)
            return PolicyConfig.Disabled;
        var options = new Dictionary<string, OptionValue>();
        foreach (var (id, editor) in _editors)
        {
            if (editor.Get() is { } value)
                options[id] = value;
        }
        return PolicyConfig.Enabled(options);
    }

    private void Accept()
    {
        var config = Build();
        var problems = ChangeBuilder.Missing(_pol, config);
        if (problems.Count > 0)
        {
            _problems.Text = string.Join("  ", problems);
            return;
        }
        Result = _ws.Builder.FromConfig(_pol, _loadedScope, config);
        DialogResult = DialogResult.OK;
    }

    private void UpdateEnabled()
    {
        foreach (Control c in _options.Controls)
            c.Enabled = _enabled.Checked;
    }

    private void FitEditors()
    {
        var width = _options.ClientSize.Width - LogicalToDeviceUnits(24);
        foreach (Control c in _options.Controls)
        {
            if (c is Label label)
                label.MaximumSize = new Size(width, 0);
            else if (c is not CheckBox && c is not FlowLayoutPanel && c is not NumericUpDown)
                c.Width = width;
        }
    }

    private void BuildEditors()
    {
        var shown = new HashSet<string>();
        foreach (var item in _pol.Pres)
        {
            if (item.Text is { } caption)
            {
                AddLabel(caption);
            }
            else if (item.Ref is { } id && _pol.Element(id) is { } el && shown.Add(el.Id))
            {
                AddEditor(el);
            }
        }
        foreach (var el in _pol.Elements.Where(e => !shown.Contains(e.Id)))
            AddEditor(el);
        if (_pol.Elements.Count == 0)
            AddLabel("This policy has no options.");
    }

    private void AddLabel(string text) =>
        _options.Controls.Add(new Label { Text = text, AutoSize = true, Margin = new Padding(0, 6, 0, 2) });

    private void AddEditor(ElementDef el)
    {
        var label = el.Label + (el.Required ? " *" : "");
        switch (el.Type)
        {
            case ElementDef.Enum:
                AddLabel(label);
                var combo = new ComboBox { Name = "opt_" + el.Id, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = el.Label, DisplayMember = "Display" };
                var choices = el.Items.Select((it, i) => new Choice(i, it.Display));
                // The editor sorts choices unless the template asks it not to.
                combo.Items.AddRange((el.Ui?.NoSort == true ? choices : choices.OrderBy(c => c.Display, StringComparer.CurrentCultureIgnoreCase))
                    .Cast<object>().ToArray());
                _options.Controls.Add(combo);
                _editors[el.Id] = new Editor(
                    () => combo.SelectedItem is Choice c ? new EnumOption(c.Index) : null,
                    v => combo.SelectedItem = v is EnumOption e ? combo.Items.Cast<Choice>().FirstOrDefault(c => c.Index == e.Index) : null);
                break;
            case ElementDef.Boolean:
                var box = new CheckBox { Name = "opt_" + el.Id, Text = label, AutoSize = true, AccessibleName = el.Label, Margin = new Padding(0, 8, 0, 2) };
                _options.Controls.Add(box);
                _editors[el.Id] = new Editor(() => new BoolOption(box.Checked), v => box.Checked = v is BoolOption { Checked: true });
                break;
            case ElementDef.Decimal or ElementDef.LongDecimal:
                AddLabel(label + $"  ({el.Min} – {el.Max})");
                var number = new NumericUpDown
                {
                    Minimum = el.Min, Maximum = Math.Max(el.Min, el.Max), Increment = el.Ui?.SpinStep ?? 1,
                    Width = LogicalToDeviceUnits(140), Name = "opt_" + el.Id, AccessibleName = el.Label, ThousandsSeparator = false,
                };
                _options.Controls.Add(number);
                _editors[el.Id] = new Editor(() => new NumberOption((long)number.Value),
                    v => number.Value = Math.Clamp(v is NumberOption n ? n.Value : el.Min, (long)number.Minimum, (long)number.Maximum));
                break;
            case ElementDef.Text:
                AddLabel(label);
                var text = new TextBox { Name = "opt_" + el.Id, MaxLength = el.TextLimit, AccessibleName = el.Label };
                _options.Controls.Add(text);
                _editors[el.Id] = new Editor(() => new TextOption(text.Text), v => text.Text = (v as TextOption)?.Text ?? "");
                break;
            case ElementDef.MultiText:
                AddLabel(label + "  (one per line)");
                var lines = new TextBox
                {
                    Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical,
                    Height = LogicalToDeviceUnits(90), Name = "opt_" + el.Id, AccessibleName = el.Label,
                };
                _options.Controls.Add(lines);
                _editors[el.Id] = new Editor(
                    () => new MultiTextOption(lines.Lines.Where(l => l.Length > 0).ToList()),
                    v => lines.Lines = (v as MultiTextOption)?.Lines.ToArray() ?? []);
                break;
            case ElementDef.List:
                AddLabel(label);
                var items = new List<KeyValuePair<string, string>>();
                var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
                var show = new Button { Name = "opt_" + el.Id, Text = "Show…", AutoSize = true, AccessibleName = el.Label };
                var count = new Label { AutoSize = true, Margin = new Padding(8, 8, 0, 0) };
                void Count() => count.Text = items.Count == 1 ? "1 entry" : $"{items.Count} entries";
                show.Click += (_, _) => BeginInvoke(() =>
                {
                    using var dialog = new ListEditorDialog(el, items);
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        items = dialog.Items;
                        Count();
                    }
                });
                row.Controls.AddRange([show, count]);
                _options.Controls.Add(row);
                _editors[el.Id] = new Editor(() => new ListOption(items), v =>
                {
                    items = (v as ListOption)?.Items.ToList() ?? [];
                    Count();
                });
                break;
        }
    }

    private sealed record Choice(int Index, string Display);

    private sealed record Editor(Func<OptionValue?> Get, Action<OptionValue?> Set);
}

/// <summary>The entries of a list option, one per row; with names when the template asks for them.</summary>
internal sealed class ListEditorDialog : Form
{
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = true, AllowUserToDeleteRows = true, RowHeadersWidth = 28,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, AccessibleName = "Entries",
    };
    private readonly bool _named;

    public ListEditorDialog(ElementDef el, IEnumerable<KeyValuePair<string, string>> items)
    {
        _named = el.ExplicitValue;
        Text = el.Label;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(LogicalToDeviceUnits(560), LogicalToDeviceUnits(380));
        if (_named)
            _grid.Columns.Add("name", "Value name");
        _grid.Columns.Add("value", "Value");
        foreach (var item in items)
            _grid.Rows.Add(_named ? [item.Key, item.Value] : [item.Value]);

        var hint = new Label
        {
            Dock = DockStyle.Top, Height = LogicalToDeviceUnits(24),
            Text = el.Additive ? "These entries are added to what is already there."
                : "These entries replace what is already there.",
        };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, AutoSize = true };
        buttons.Controls.AddRange([cancel, ok]);
        AcceptButton = ok;
        CancelButton = cancel;
        Controls.Add(_grid);
        Controls.Add(hint);
        Controls.Add(buttons);
        Theme.Current.Apply(this);
        _grid.BackgroundColor = Theme.Current.Bg;
        _grid.DefaultCellStyle.BackColor = Theme.Current.Bg;
        _grid.DefaultCellStyle.ForeColor = Theme.Current.Fg;
    }

    public List<KeyValuePair<string, string>> Items =>
        _grid.Rows.Cast<DataGridViewRow>()
            .Where(r => !r.IsNewRow)
            .Select(r => new KeyValuePair<string, string>(
                _named ? r.Cells[0].Value?.ToString()?.Trim() ?? "" : "",
                r.Cells[_named ? 1 : 0].Value?.ToString()?.Trim() ?? ""))
            .Where(p => p.Key.Length > 0 || p.Value.Length > 0)
            .ToList();
}
