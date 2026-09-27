using System.Text;
using GpoCatalogue.Core;
using GpoCatalogue.Core.Changes;
using GpoCatalogue.Core.Gpo;
using GpoCatalogue.Core.Model;
using GpoCatalogue.Core.View;

namespace GpoCatalogue.App;

/// <summary>
/// Shows every staged change with the registry values it touches, and writes the ones that stay ticked.
/// Also used to restore a backup, with the backup's differences in place of the staged changes.
/// </summary>
internal sealed class ReviewDialog : Form
{
    private readonly Workspace _ws;
    private readonly bool _canApply;
    private readonly ChangePlan? _restore;
    private readonly string _note;
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
        RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None, BorderStyle = BorderStyle.None,
        EditMode = DataGridViewEditMode.EditOnEnter, Name = "changes", AccessibleName = "Changes",
    };
    private readonly TextBox _details = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill,
        Name = "details", AccessibleName = "Change details",
    };
    private readonly Label _summary = new() { Name = "summary", AutoSize = true };
    private readonly Label _obstacles = new() { Name = "obstacles", AutoSize = true };
    private readonly Button _apply = new() { Name = "apply", AutoSize = true };
    private readonly Button _elevate = new() { Name = "elevate", Text = "Restart as &administrator", AutoSize = true };
    private readonly HashSet<(string, Scope)> _excluded = [];
    private List<object> _rows = [];
    private ChangePlan _display = null!;
    private ChangePlan _plan = null!;
    private bool _loading;

    public ReviewDialog(Workspace ws, bool canApply, ChangePlan? restore = null, string? restoreName = null)
    {
        _ws = ws;
        _canApply = canApply;
        _restore = restore;
        _note = restore is null ? "Before applying staged changes" : "Before restoring backup " + restoreName;
        Text = restore is null ? "Review and apply changes" : "Restore backup " + restoreName;
        AccessibleName = Text;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = Theme.Current.Body;
        ClientSize = new Size(LogicalToDeviceUnits(980), LogicalToDeviceUnits(640));
        MinimumSize = new Size(LogicalToDeviceUnits(700), LogicalToDeviceUnits(450));

        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "include", HeaderText = "Apply", Width = LogicalToDeviceUnits(50), Visible = restore is null });
        AddText("policy", "Policy", 250);
        AddText("scope", "Section", 75);
        AddText("source", "From", 110);
        AddText("current", "Current", 200);
        AddText("new", "New", 200);
        AddText("notes", "Notes", 150);
        _grid.CellValueChanged += (_, e) =>
        {
            if (!_loading && e.ColumnIndex == 0 && e.RowIndex >= 0 && _rows[e.RowIndex] is PlannedChange c)
            {
                var id = (c.Policy.Uid, c.Scope);
                if (_grid.Rows[e.RowIndex].Cells[0].Value is true)
                    _excluded.Remove(id);
                else
                    _excluded.Add(id);
                Replan();
            }
        };
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _grid.SelectionChanged += (_, _) => ShowDetails();

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        split.Panel1.Controls.Add(_grid);
        split.Panel2.Controls.Add(_details);

        var cancel = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        _apply.Click += (_, _) => Apply();
        _elevate.Click += (_, _) => DialogResult = DialogResult.Retry;
        CancelButton = cancel;
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
        buttons.Controls.AddRange([cancel, _apply, _elevate]);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(LogicalToDeviceUnits(10)) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_summary, 0, 0);
        layout.SetColumnSpan(_summary, 2);
        layout.Controls.Add(_obstacles, 0, 1);
        layout.SetColumnSpan(_obstacles, 2);
        layout.Controls.Add(split, 0, 2);
        layout.SetColumnSpan(split, 2);
        layout.Controls.Add(buttons, 1, 3);
        Controls.Add(layout);

        Theme.Current.Apply(this);
        StyleGrid();
        _details.Font = Theme.Current.Mono;
        _details.BackColor = Theme.Current.Head;
        _obstacles.ForeColor = Theme.Current.FlagFg;
        _summary.MaximumSize = _obstacles.MaximumSize = new Size(LogicalToDeviceUnits(950), 0);
        Shown += (_, _) => split.SplitterDistance = (int)(split.Height * 0.58);
        Replan();
    }

    /// <summary>The result of applying, when it was applied.</summary>
    public ApplyOutcome? Outcome { get; private set; }

    /// <summary>The staged changes that were written and can be dropped from the list.</summary>
    public List<PendingChange> Written { get; } = [];

    private void AddText(string name, string header, int width) =>
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = name, HeaderText = header, ReadOnly = true, Width = LogicalToDeviceUnits(width),
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });

    private void StyleGrid()
    {
        var t = Theme.Current;
        _grid.EnableHeadersVisualStyles = false;
        _grid.BackgroundColor = t.Bg;
        _grid.GridColor = t.Line;
        _grid.DefaultCellStyle.BackColor = t.Bg;
        _grid.DefaultCellStyle.ForeColor = t.Fg;
        _grid.DefaultCellStyle.SelectionBackColor = t.Selection;
        _grid.DefaultCellStyle.SelectionForeColor = t.Fg;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = t.Head;
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = t.Fg;
        _grid.ColumnHeadersDefaultCellStyle.Font = t.Bold;
        _grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
    }

    private void Replan()
    {
        if (_restore is not null)
        {
            _display = _plan = _restore;
        }
        else
        {
            var all = _ws.Pending.All.ToList();
            _display = _ws.Planner.Plan(all);
            _plan = _ws.Planner.Plan(all.Where(c => !_excluded.Contains((c.Uid, c.Scope))));
        }

        var current = _grid.CurrentCell?.RowIndex ?? 0;
        _loading = true;
        _grid.Rows.Clear();
        _rows = [];
        var t = Theme.Current;
        foreach (var c in _display.Changes.OrderBy(c => c.Policy.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(c => c.Scope))
        {
            var included = c.CanApply && !_excluded.Contains((c.Policy.Uid, c.Scope));
            var notes = c.Change.NeedsInput ? "needs input" : c.Warnings.Count switch { 0 => "", 1 => "1 note", var n => $"{n} notes" };
            var index = _grid.Rows.Add(included, c.Policy.Name, c.Scope.Display(), c.Change.Source.Display(), c.Current, c.New, notes);
            _rows.Add(c);
            var row = _grid.Rows[index];
            if (!c.CanApply)
            {
                row.Cells[0].ReadOnly = true;
                row.DefaultCellStyle.ForeColor = t.Muted;
            }
            if (c.Warnings.Count > 0)
                row.Cells["notes"].Style.ForeColor = c.Change.NeedsInput ? t.FlagFg : t.MidFg;
        }
        foreach (var s in _plan.SideEffects)
        {
            var index = _grid.Rows.Add(false, s.Policy.Name, s.Scope.Display(), "(shares values)", s.Before, s.After, "changes along");
            _rows.Add(s);
            _grid.Rows[index].Cells[0].ReadOnly = true;
            _grid.Rows[index].DefaultCellStyle.ForeColor = t.MidFg;
        }
        if (_display.OtherDiffs.Count > 0)
        {
            _grid.Rows.Add(false, "Entries of no known policy", "", "Backup", "", $"{_display.OtherDiffs.Count} values", "");
            _rows.Add(_display.OtherDiffs);
            _grid.Rows[^1].Cells[0].ReadOnly = true;
        }
        _loading = false;
        if (_grid.Rows.Count > 0)
            _grid.CurrentCell = _grid.Rows[Math.Min(current, _grid.Rows.Count - 1)].Cells[1];

        var count = _plan.Changes.Count(c => c.CanApply && !_excluded.Contains((c.Policy.Uid, c.Scope)));
        if (_restore is not null)
            count = _plan.Changes.Count;
        var sections = string.Join(" and ", _plan.Sections.Keys.Select(s => s.Display()));
        var target = _ws.States.Store.IsTestFolder ? "the test folder " + _ws.States.Store.Folder : "the local group policy of this computer";
        _summary.Text = _plan.HasWork
            ? $"{(_restore is null ? count + " change" + (count == 1 ? "" : "s") : "Restoring")} to {target}, "
              + $"{sections} section{(_plan.Sections.Count == 1 ? "" : "s")}. The current policy files are backed up first."
            : "Nothing to write: the ticked changes leave the policy as it is.";

        var obstacles = _plan.HasWork ? _ws.Applier.Preflight(_plan).ToList() : [];
        var excludedIncomplete = _display.Changes.Count(c => !c.CanApply);
        var lines = obstacles.Select(o => (o.Blocking ? "Cannot apply: " : "Note: ") + o.Message).ToList();
        if (excludedIncomplete > 0)
            lines.Add($"{excludedIncomplete} change{(excludedIncomplete == 1 ? " needs" : "s need")} input and will not be applied until edited.");
        if (_plan.SideEffects.Count > 0)
            lines.Add($"{_plan.SideEffects.Count} other polic{(_plan.SideEffects.Count == 1 ? "y changes" : "ies change")} too, because they share registry values.");
        _obstacles.Text = string.Join("\n", lines);

        var blocking = obstacles.Any(o => o.Blocking);
        var needsElevation = !_ws.States.Store.IsTestFolder && !ElevationHelper.IsElevated;
        _elevate.Visible = needsElevation;
        _apply.Text = _restore is null ? $"&Apply {count} change{(count == 1 ? "" : "s")}" : "&Restore";
        _apply.Enabled = _canApply && _plan.HasWork && !blocking;
        _apply.Visible = !needsElevation;
        AcceptButton = _apply.Enabled ? _apply : null;
        ShowDetails();
    }

    private void ShowDetails()
    {
        if (_loading)
            return;
        var index = _grid.CurrentCell?.RowIndex ?? -1;
        if (index < 0 || index >= _rows.Count)
        {
            _details.Text = "";
            return;
        }
        var sb = new StringBuilder();
        switch (_rows[index])
        {
            case PlannedChange c:
                sb.AppendLine($"{c.Policy.Name}  [{c.Policy.Uid}, {c.Scope.Display()}]");
                sb.AppendLine($"Current: {c.Current}");
                sb.AppendLine($"New:     {c.New}");
                foreach (var w in c.Warnings)
                    sb.AppendLine("Note:    " + w);
                sb.AppendLine();
                sb.AppendLine(c.Diffs.Count == 0 ? "No registry values change." : "Registry values in the policy file:");
                foreach (var d in c.Diffs)
                    sb.AppendLine("  " + d.Line);
                break;
            case SideEffect s:
                sb.AppendLine($"{s.Policy.Name}  [{s.Policy.Uid}, {s.Scope.Display()}]");
                sb.AppendLine("Not selected, but it owns some of the same registry values, so it changes as well.");
                sb.AppendLine($"Now:   {s.Before}");
                sb.AppendLine($"After: {s.After}");
                break;
            case IReadOnlyList<EntryDiff> other:
                sb.AppendLine("Entries in the policy file that no policy in the catalogue accounts for:");
                foreach (var d in other)
                    sb.AppendLine("  " + d.Line);
                break;
        }
        _details.Text = sb.ToString();
    }

    private async void Apply()
    {
        var plan = _plan;
        UseWaitCursor = true;
        Enabled = false;
        _summary.Text = "Writing policy and waiting for Windows to apply it…";
        try
        {
            Outcome = await Task.Run(() => _ws.Applier.Apply(plan, _note, waitForRefresh: true));
        }
        catch (Exception ex) when (ex is GpoException or IOException or UnauthorizedAccessException)
        {
            Enabled = true;
            UseWaitCursor = false;
            MessageBox.Show(this, "Nothing was applied.\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Replan();
            return;
        }
        UseWaitCursor = false;
        Enabled = true;

        var failed = Outcome.NotAsPlanned.Select(c => (c.Policy.Uid, c.Scope)).ToHashSet();
        if (_restore is null)
        {
            Written.AddRange(plan.Changes.Where(c => c.CanApply && !failed.Contains((c.Policy.Uid, c.Scope)))
                .Select(c => c.Change));
        }

        var text = new StringBuilder();
        text.AppendLine(Outcome.Succeeded
            ? $"Applied. The previous policy is saved as backup {Outcome.Backup.Id}."
            : $"Written, but {Outcome.NotAsPlanned.Count} polic{(Outcome.NotAsPlanned.Count == 1 ? "y" : "ies")} did not end up as planned. The previous policy is saved as backup {Outcome.Backup.Id}.");
        foreach (var c in Outcome.NotAsPlanned)
            text.AppendLine("  • " + c.Policy.Name);
        if (Outcome.RefreshError is { } refresh)
            text.AppendLine().AppendLine("Windows could not be asked to apply policy now: " + refresh + " It will apply at the next refresh.");
        else if (Outcome.NotYetApplied.Count > 0)
        {
            text.AppendLine().AppendLine($"The registry does not show {Outcome.NotYetApplied.Count} of the changes yet:");
            foreach (var c in Outcome.NotYetApplied.Take(10))
                text.AppendLine("  • " + c.Policy.Name);
        }
        MessageBox.Show(this, text.ToString(), Text, MessageBoxButtons.OK,
            Outcome.Succeeded ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        DialogResult = DialogResult.OK;
    }
}

/// <summary>The copies of the policy files taken before each change.</summary>
internal sealed class BackupsDialog : Form
{
    private readonly Workspace _ws;
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false,
        Name = "backupList", AccessibleName = "Backups",
    };

    public BackupsDialog(Workspace ws, bool canApply)
    {
        _ws = ws;
        Text = "Backups";
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(LogicalToDeviceUnits(720), LogicalToDeviceUnits(400));
        _list.Columns.Add("Taken", LogicalToDeviceUnits(150));
        _list.Columns.Add("Before", LogicalToDeviceUnits(300));
        _list.Columns.Add("Computer values", LogicalToDeviceUnits(110));
        _list.Columns.Add("User values", LogicalToDeviceUnits(90));
        foreach (var b in ws.Backups.List())
        {
            _list.Items.Add(new ListViewItem([b.Created.ToString("yyyy-MM-dd HH:mm:ss"), b.Note,
                b.MachineEntries.ToString(), b.UserEntries.ToString()]) { Tag = b });
        }

        var info = new Label
        {
            Dock = DockStyle.Top, Height = LogicalToDeviceUnits(40), Padding = new Padding(0, 4, 0, 4),
            Text = $"A copy of the policy files is taken before every change, in {ws.Backups.Root}. "
                   + "Restoring shows the differences first and is itself backed up.",
        };
        var restore = new Button { Name = "restore", Text = "&Restore…", AutoSize = true, Enabled = false };
        var open = new Button { Text = "Open &folder", AutoSize = true };
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        _list.SelectedIndexChanged += (_, _) => restore.Enabled = _list.SelectedItems.Count == 1;
        _list.DoubleClick += (_, _) => restore.PerformClick();
        restore.Click += (_, _) => BeginInvoke(() => Restore(canApply));
        open.Click += (_, _) =>
        {
            Directory.CreateDirectory(ws.Backups.Root);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ws.Backups.Root) { UseShellExecute = true });
        };
        CancelButton = close;
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, AutoSize = true };
        buttons.Controls.AddRange([close, open, restore]);
        Controls.Add(_list);
        Controls.Add(info);
        Controls.Add(buttons);
        Padding = new Padding(LogicalToDeviceUnits(10));
        Theme.Current.Apply(this);
        _list.BackColor = Theme.Current.Bg;
        _list.ForeColor = Theme.Current.Fg;
        if (_list.Items.Count == 0)
            info.Text += "\nNo backups yet.";
    }

    /// <summary>Set when a restore was applied or when the user asked to restart with administrator rights.</summary>
    public DialogResult Outcome { get; private set; } = DialogResult.None;

    private void Restore(bool canApply)
    {
        if (_list.SelectedItems.Count != 1 || _list.SelectedItems[0].Tag is not BackupInfo backup)
            return;
        var plan = _ws.Planner.PlanRestore(_ws.Backups.Load(backup.Id));
        if (!plan.HasWork)
        {
            MessageBox.Show(this, "The policy already matches this backup.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var review = new ReviewDialog(_ws, canApply, plan, backup.Created.ToString("yyyy-MM-dd HH:mm:ss"));
        var result = review.ShowDialog(this);
        if (result is DialogResult.OK or DialogResult.Retry)
        {
            Outcome = result;
            DialogResult = DialogResult.OK;
        }
    }
}
