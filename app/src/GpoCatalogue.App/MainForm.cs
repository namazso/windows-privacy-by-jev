using GpoCatalogue.Core;
using GpoCatalogue.Core.Changes;
using GpoCatalogue.Core.Model;
using GpoCatalogue.Core.View;

namespace GpoCatalogue.App;

internal sealed class MainForm : Form
{
    private readonly Workspace _ws;
    private readonly StartOptions _options;
    private readonly CatalogGrid _grid;
    private readonly FilterBar _filters;
    private readonly DetailPane _detail;
    private readonly SplitContainer _split = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
    private readonly ToolStripTextBox _search = new() { Name = "search", AutoSize = false, AccessibleName = "Search all policies" };
    private readonly ToolStripComboBox _preset = new() { Name = "preset", DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Quick presets" };
    private readonly ToolStripDropDownButton _columnsMenu = new("Columns") { Name = "columns", AccessibleName = "Columns" };
    private readonly ToolStripButton _edit = new("&Edit…") { Name = "edit", AccessibleName = "Edit", ToolTipText = "Set the selected policy (Enter or double-click)" };
    private readonly ToolStripDropDownButton _copy = new("&Copy from") { Name = "copy", AccessibleName = "Copy from", ToolTipText = "Stage the setting of a baseline or of the model for the selected policies" };
    private readonly ToolStripButton _notConfigured = new("&Not configured") { Name = "notConfigured", AccessibleName = "Not configured", ToolTipText = "Stage Not configured for the selected policies" };
    private readonly ToolStripButton _discard = new("&Discard") { Name = "discard", AccessibleName = "Discard", ToolTipText = "Drop the staged changes of the selected policies" };
    private readonly ToolStripButton _review = new("&Review and apply…") { Name = "review", AccessibleName = "Review and apply", Font = Theme.Current.Bold };
    private readonly ToolStripButton _backups = new("&Backups…") { Name = "backups", AccessibleName = "Backups" };
    private readonly Label _count = new() { Name = "count", Dock = DockStyle.Top, AutoSize = false };
    private readonly Panel _banner = new() { Name = "banner", Dock = DockStyle.Top, Visible = false };
    private readonly Label _bannerText = new() { Name = "bannerText", AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button _elevate = new() { Name = "elevate", Text = "Restart as &administrator", AutoSize = true, Dock = DockStyle.Right };
    private readonly ToolStripStatusLabel _status = new() { Name = "status", Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _pendingStatus = new() { Name = "pendingStatus" };
    private readonly System.Windows.Forms.Timer _searchDebounce = new() { Interval = 150 };
    private readonly System.Windows.Forms.Timer _watchDebounce = new() { Interval = 800 };
    private FileSystemWatcher? _watcher;
    private bool _presetSyncing;
    private int _rowsChangedQueued;

    public MainForm(Workspace ws, StartOptions options)
    {
        _ws = ws;
        _options = options;
        CanApply = ws.States.Store.IsTestFolder || (ElevationHelper.IsElevated && !options.ReadOnly);
        Text = "GPO Catalogue – Windows 11 24H2 Group Policies"
               + (ws.States.Store.IsTestFolder ? " [TEST FOLDER]" : ElevationHelper.IsElevated ? " (Administrator)" : "");
        Font = Theme.Current.Body;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(LogicalToDeviceUnits(760), LogicalToDeviceUnits(520));
        KeyPreview = true;
        PlaceWindow();

        _grid = new CatalogGrid(ws) { Name = "grid", Dock = DockStyle.Fill };
        _filters = new FilterBar(_grid, ws);
        _detail = new DetailPane(ws);

        _split.Panel1.Controls.Add(_grid);
        _split.Panel1.Controls.Add(_filters);
        _split.Panel2.Controls.Add(_detail);

        var header = BuildHeader();
        var viewStrip = BuildViewStrip();
        var actionStrip = BuildActionStrip();
        BuildBanner();
        _count.Height = LogicalToDeviceUnits(24);
        _count.Padding = new Padding(LogicalToDeviceUnits(10), LogicalToDeviceUnits(4), 0, 0);
        var status = new StatusStrip { Renderer = new ThemedRenderer(), SizingGrip = true };
        status.Items.AddRange([_status, _pendingStatus]);

        // Docked controls are laid out from the last added to the first.
        Controls.Add(_split);
        Controls.Add(_count);
        Controls.Add(_banner);
        Controls.Add(actionStrip);
        Controls.Add(viewStrip);
        Controls.Add(header);
        Controls.Add(status);

        Theme.Current.Apply(this);
        _banner.BackColor = Theme.Current.MidBg;
        _bannerText.ForeColor = Theme.Current.MidFg;
        _count.ForeColor = Theme.Current.Muted;
        _grid.ApplyTheme();

        WireEvents();
        Load += (_, _) => OnLoaded();
    }

    /// <summary>True when changes can be written: as administrator, or to a test folder.</summary>
    public bool CanApply { get; }

    private Control BuildHeader()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1,
            Padding = new Padding(LogicalToDeviceUnits(10), LogicalToDeviceUnits(8), LogicalToDeviceUnits(10), LogicalToDeviceUnits(4)),
        };
        panel.Controls.Add(new Label { Text = "Windows 11 24H2 Group Policies", Font = Theme.Current.Title, AutoSize = true, AccessibleName = "Title" });
        var meta = new Label { Text = _ws.MetaLine, AutoSize = true, AccessibleName = "Catalogue summary", Tag = "muted" };
        panel.Controls.Add(meta);
        panel.HandleCreated += (_, _) => meta.ForeColor = Theme.Current.Muted;
        return panel;
    }

    private ToolStrip BuildViewStrip()
    {
        var strip = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Renderer = new ThemedRenderer(), Padding = new Padding(LogicalToDeviceUnits(6), 2, 6, 2) };
        _search.Width = LogicalToDeviceUnits(280);
        _search.TextBox.PlaceholderText = "Search name, category, explanation…";
        foreach (var p in Preset.All)
            _preset.Items.Add(p);
        _preset.ComboBox.DisplayMember = nameof(Preset.Label);
        _preset.ComboBox.Width = LogicalToDeviceUnits(210);
        _preset.SelectedIndex = 0;
        foreach (var def in _ws.Columns.All)
        {
            var item = new ToolStripMenuItem(def.Label) { CheckOnClick = true, Checked = !_ws.View.Hidden.Contains(def.Id), Tag = def.Id };
            item.CheckedChanged += (_, _) => SetColumnVisible((string)item.Tag!, item.Checked);
            _columnsMenu.DropDownItems.Add(item);
        }
        var export = new ToolStripButton("Export CSV…");
        export.Click += (_, _) => BeginInvoke(ExportCsv);
        var reset = new ToolStripButton("Reset") { ToolTipText = "Clear filters, search and sorting; show the default columns" };
        reset.Click += (_, _) => ResetView();
        var refresh = new ToolStripButton("Refresh") { ToolTipText = "Read the policy and the registry again (F5)" };
        refresh.Click += (_, _) => RefreshState();
        var help = new ToolStripButton("How to read") { ToolTipText = "Explains the columns" };
        help.Click += (_, _) => { _grid.ClearSelection(); _grid.CurrentCell = null; _detail.Show(null, CanApply); };
        strip.Items.AddRange([_search, new ToolStripLabel("Preset"), _preset, _columnsMenu, new ToolStripSeparator(),
            export, reset, refresh, help]);
        return strip;
    }

    private ToolStrip BuildActionStrip()
    {
        var strip = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Renderer = new ThemedRenderer(), Padding = new Padding(LogicalToDeviceUnits(6), 2, 6, 2) };
        _edit.Click += (_, _) => BeginInvoke(EditCurrent);
        foreach (var (label, source) in new[]
                 {
                     ("Security Baseline", ChangeSource.SecurityBaseline),
                     ("RTLFB", ChangeSource.Rtlfb),
                     ("Model recommendation (privacy)", ChangeSource.ModelPrivacy),
                     ("Model recommendation (security)", ChangeSource.ModelSecurity),
                 })
        {
            _copy.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => CopyFrom(source)));
        }
        _notConfigured.Click += (_, _) => Stage(_ws.Builder.NotConfigured(Selected()), ChangeSource.NotConfigured);
        _discard.Click += (_, _) => DiscardSelected();
        _review.Click += (_, _) => BeginInvoke(Review);
        _backups.Click += (_, _) => BeginInvoke(ShowBackups);
        strip.Items.AddRange([_edit, _copy, _notConfigured, _discard, new ToolStripSeparator(), _review,
            new ToolStripSeparator(), _backups]);

        var menu = new ContextMenuStrip { Renderer = new ThemedRenderer() };
        menu.Items.Add("Edit…", null, (_, _) => BeginInvoke(EditCurrent));
        var copy = new ToolStripMenuItem("Copy from");
        foreach (ToolStripMenuItem item in _copy.DropDownItems)
        {
            var source = item;
            copy.DropDownItems.Add(item.Text, null, (_, _) => source.PerformClick());
        }
        menu.Items.Add(copy);
        menu.Items.Add("Not configured", null, (_, _) => _notConfigured.PerformClick());
        menu.Items.Add("Discard staged change", null, (_, _) => DiscardSelected());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Copy uid", null, (_, _) =>
        {
            var uids = string.Join(Environment.NewLine, Selected().Select(p => p.Uid));
            if (uids.Length > 0)
                Clipboard.SetText(uids);
        });
        _grid.ContextMenuStrip = menu;
        return strip;
    }

    private void BuildBanner()
    {
        _banner.Height = LogicalToDeviceUnits(36);
        _banner.Padding = new Padding(LogicalToDeviceUnits(10), LogicalToDeviceUnits(4), LogicalToDeviceUnits(10), LogicalToDeviceUnits(4));
        _banner.Controls.Add(_bannerText);
        _banner.Controls.Add(_elevate);
        _elevate.Click += (_, _) => RestartElevated();
        if (_ws.States.Store.IsTestFolder)
        {
            _bannerText.Text = $"Test folder: changes are written to {_ws.States.Store.Folder}, not to the policy of this computer.";
            _elevate.Visible = false;
            _banner.Visible = true;
        }
        else if (!CanApply)
        {
            _bannerText.Text = _options.ReadOnly
                ? "Read-only: started with --read-only. Changes can be staged and reviewed but not applied."
                : "Read-only: not running as administrator. You can stage and review changes; applying them needs administrator rights.";
            _elevate.Visible = !ElevationHelper.IsElevated;
            _banner.Visible = true;
        }
    }

    private void WireEvents()
    {
        _search.TextChanged += (_, _) =>
        {
            _searchDebounce.Stop();
            _searchDebounce.Start();
        };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            _ws.View.Search = _search.Text.Trim().ToLowerInvariant();
            Recompute();
        };
        _preset.SelectedIndexChanged += (_, _) =>
        {
            if (_presetSyncing || _preset.SelectedItem is not Preset preset)
                return;
            preset.Apply(_ws.View);
            _search.Text = _ws.View.Search;
            _searchDebounce.Stop();
            SyncControlsFromView();
            Recompute();
        };
        _filters.Changed += (_, _) => Recompute();
        _grid.SortRequested += (_, column) =>
        {
            _ws.View.ToggleSort(column);
            Recompute();
        };
        _grid.SelectionChanged += (_, _) => ShowCurrent();
        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0)
                EditCurrent();
        };
        _grid.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = e.SuppressKeyPress = true;
                EditCurrent();
            }
        };
        _ws.RowsChanged += (_, _) =>
        {
            if (!IsHandleCreated)
                return;
            // Several changes in a row, for example while an apply waits for Windows, need one redraw.
            if (Interlocked.Exchange(ref _rowsChangedQueued, 1) == 0)
                BeginInvoke(AfterRowsChanged);
        };
        _ws.Pending.Changed += (_, _) =>
        {
            try
            {
                _ws.Pending.Save(_ws.Paths.Pending);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The staged changes stay in memory; only a restart would lose them.
            }
        };
        _watchDebounce.Tick += (_, _) =>
        {
            _watchDebounce.Stop();
            RefreshState(quiet: true);
        };
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F5)
            {
                RefreshState();
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == Keys.F)
            {
                _search.Focus();
                e.Handled = true;
            }
        };
        FormClosing += (_, _) => SaveView();
    }

    private void OnLoaded()
    {
        var loaded = _ws.Pending.Load(_options.PendingFile ?? _ws.Paths.Pending, _ws.Catalog);
        if (_options.PendingFile is not null && _options.PendingFile != _ws.Paths.Pending)
            _ws.Pending.Save(_ws.Paths.Pending);
        _search.Text = _ws.View.Search;
        SyncControlsFromView();
        _split.SplitterDistance = Math.Clamp(
            _ws.View.DetailHeight > 0 ? _split.Height - LogicalToDeviceUnits(_ws.View.DetailHeight) : (int)(_split.Height * 0.6),
            LogicalToDeviceUnits(150), Math.Max(LogicalToDeviceUnits(150), _split.Height - LogicalToDeviceUnits(80)));
        Recompute();
        if (_options.Select is { } uid)
        {
            if (!_ws.Visible.Any(r => r.Uid == uid))
            {
                _ws.View.ClearFilters();
                _search.Text = "";
                SyncControlsFromView();
                Recompute();
            }
            _grid.Select(uid);
        }
        ShowCurrent();
        _filters.Align();
        StartWatching();
        SetStatus(loaded > 0 ? $"{loaded} staged change{(loaded == 1 ? "" : "s")} carried over from the last session." : "Ready.");
    }

    private void AfterRowsChanged()
    {
        Interlocked.Exchange(ref _rowsChangedQueued, 0);
        // The set of rows can change with the state (state and pending filters), so filter again.
        Recompute(keepStatus: true);
    }

    private void Recompute(bool keepStatus = false)
    {
        _ws.Compute();
        _grid.Reload();
        UpdateCommands();
        ShowCurrent();
        if (!keepStatus)
            SetStatus("");
    }

    private void SyncControlsFromView()
    {
        _filters.SyncFromView();
        _presetSyncing = true;
        if (!_ws.View.HasFilters)
            _preset.SelectedIndex = 0;
        _presetSyncing = false;
        foreach (ToolStripMenuItem item in _columnsMenu.DropDownItems)
            item.Checked = !_ws.View.Hidden.Contains((string)item.Tag!);
    }

    private void ShowCurrent()
    {
        _detail.Show(_grid.CurrentPolicy, CanApply);
        UpdateCommands();
    }

    private void UpdateCommands()
    {
        var selected = _grid.SelectedPolicies();
        _edit.Enabled = _grid.CurrentPolicy is not null;
        _copy.Enabled = _notConfigured.Enabled = selected.Count > 0;
        _discard.Enabled = selected.Any(r => r.HasPending);
        var count = _ws.Pending.Count;
        var incomplete = _ws.Pending.All.Count(c => c.NeedsInput);
        _review.Text = count == 0 ? "&Review and apply…" : $"&Review and apply ({count})…";
        _review.Enabled = count > 0;
        _pendingStatus.Text = count == 0 ? "No staged changes"
            : $"{count} staged" + (incomplete > 0 ? $", {incomplete} need input" : "");
        var text = $"{_ws.Visible.Count} matching policies ({_ws.Rows.Count} total)";
        if (selected.Count > 1)
            text += $" · {selected.Count} selected";
        _count.Text = text;
    }

    private List<PolicyDef> Selected()
    {
        var rows = _grid.SelectedPolicies();
        if (rows.Count == 0 && _grid.CurrentPolicy is { } current)
            rows.Add(current);
        return rows.Select(r => r.Def).ToList();
    }

    private void SetStatus(string text) => _status.Text = text;

    private void EditCurrent()
    {
        if (_grid.CurrentPolicy is not { } row)
            return;
        var scope = row.Pending.FirstOrDefault()?.Scope
                    ?? row.States.FirstOrDefault(s => s.IsConfigured)?.Scope
                    ?? ChangeBuilder.DefaultScope(row.Def);
        using var dialog = new EditPolicyDialog(_ws, row.Def, scope);
        if (dialog.ShowDialog(this) == DialogResult.OK && dialog.Result is { } result)
            Stage(result, ChangeSource.Manual);
    }

    private void CopyFrom(ChangeSource source)
    {
        var policies = Selected();
        var result = source is ChangeSource.SecurityBaseline or ChangeSource.Rtlfb
            ? _ws.Builder.FromBaseline(policies, source)
            : _ws.Builder.FromModel(policies, source);
        Stage(result, source);
    }

    private void Stage(StageResult result, ChangeSource source)
    {
        _ws.Stage(result);
        SetStatus(result.Staged.Count == 0 && result.Withdrawn.Count == 0 && result.Skipped.Count == 0
            ? "Nothing selected."
            : result.Describe(source) + (CanApply || result.Staged.Count == 0 ? "" : " · applying needs administrator rights"));
    }

    private void DiscardSelected()
    {
        var changes = _grid.SelectedPolicies().SelectMany(r => r.Pending).ToList();
        _ws.Pending.RemoveMany(changes);
        SetStatus($"{changes.Count} staged change{(changes.Count == 1 ? "" : "s")} discarded.");
    }

    private void Review()
    {
        if (_ws.Pending.Count == 0)
            return;
        using var dialog = new ReviewDialog(_ws, CanApply);
        var result = dialog.ShowDialog(this);
        if (result == DialogResult.Retry)
        {
            RestartElevated();
            return;
        }
        if (dialog.Outcome is { } outcome)
        {
            _ws.Pending.RemoveMany(dialog.Written);
            SetStatus($"Applied {dialog.Written.Count} change{(dialog.Written.Count == 1 ? "" : "s")}; backup {outcome.Backup.Id}.");
        }
    }

    private void ShowBackups()
    {
        using var dialog = new BackupsDialog(_ws, CanApply);
        dialog.ShowDialog(this);
        if (dialog.Outcome == DialogResult.Retry)
            RestartElevated();
        else if (dialog.Outcome == DialogResult.OK)
            SetStatus("Backup restored.");
    }

    private void RestartElevated()
    {
        SaveView();
        _ws.Pending.Save(_ws.Paths.Pending);
        var args = new List<string>(_options.Passthrough);
        if (_ws.Pending.Count > 0)
            args.AddRange(["--pending", _ws.Paths.Pending]);
        if (_grid.CurrentPolicy is { } row)
            args.AddRange(["--select", row.Uid]);
        if (ElevationHelper.RestartElevated(args))
            Close();
        else
            SetStatus("Restart as administrator was cancelled.");
    }

    private void RefreshState(bool quiet = false)
    {
        try
        {
            _ws.States.Refresh();
            if (!quiet)
                SetStatus("Read the policy and the registry again.");
            else
                SetStatus("The policy files changed; state updated.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus("Could not read the policy: " + ex.Message);
        }
    }

    private void StartWatching()
    {
        if (!Directory.Exists(_ws.States.Store.Folder))
            return;
        _watcher = new FileSystemWatcher(_ws.States.Store.Folder)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            SynchronizingObject = this,
        };
        FileSystemEventHandler changed = (_, _) =>
        {
            _watchDebounce.Stop();
            _watchDebounce.Start();
        };
        _watcher.Changed += changed;
        _watcher.Created += changed;
        _watcher.Deleted += changed;
        _watcher.EnableRaisingEvents = true;
    }

    private void SetColumnVisible(string id, bool visible)
    {
        if (visible)
            _ws.View.Hidden.Remove(id);
        else
            _ws.View.Hidden.Add(id);
        if (_grid.Columns[id] is { } column && column.Visible != visible)
            column.Visible = visible;
        _filters.Align();
    }

    private void ResetView()
    {
        _ws.View.Reset(_ws.Columns);
        foreach (DataGridViewColumn c in _grid.Columns)
        {
            var def = CatalogGrid.Def(c);
            c.Visible = !def.Hidden;
            c.Width = LogicalToDeviceUnits(def.Width);
        }
        var index = 0;
        foreach (var def in _ws.Columns.All)
            _grid.Columns[def.Id]!.DisplayIndex = index++;
        _search.Text = "";
        _ws.View.Search = "";
        SyncControlsFromView();
        Recompute();
        _filters.Align();
    }

    private void ExportCsv()
    {
        using var dialog = new SaveFileDialog
        {
            FileName = "gpo-catalogue.csv",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        try
        {
            FilterEngine.WriteCsv(dialog.FileName, _ws.Engine.ToCsv(_ws.Visible, _ws.View));
            SetStatus($"Exported {_ws.Visible.Count} rows to {dialog.FileName}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "Export CSV", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void PlaceWindow()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1024, 768);
        if (_ws.View.Window is [var x, var y, var w, var h] && area.IntersectsWith(new Rectangle(x, y, w, h)))
            Bounds = new Rectangle(x, y, w, h);
        else
            Bounds = new Rectangle(area.Left + area.Width / 20, area.Top + area.Height / 20,
                Math.Min(area.Width * 9 / 10, LogicalToDeviceUnits(1400)), area.Height * 9 / 10);
        if (_ws.View.Maximized)
            WindowState = FormWindowState.Maximized;
    }

    private void SaveView()
    {
        _grid.SaveLayout();
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        _ws.View.Window = [bounds.X, bounds.Y, bounds.Width, bounds.Height];
        _ws.View.Maximized = WindowState == FormWindowState.Maximized;
        _ws.View.DetailHeight = (int)Math.Round((_split.Height - _split.SplitterDistance) * 96.0 / DeviceDpi);
        _ws.View.Save(_ws.Paths.View);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _watcher?.Dispose();
            _searchDebounce.Dispose();
            _watchDebounce.Dispose();
        }
        base.Dispose(disposing);
    }
}
