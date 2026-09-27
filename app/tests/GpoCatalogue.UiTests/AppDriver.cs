using System.Diagnostics;
using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace GpoCatalogue.UiTests;

/// <summary>
/// Starts the program and drives it through UI Automation patterns (set value, invoke, select, expand)
/// rather than simulated mouse and keyboard, so nothing typed can end up in another window.
/// </summary>
internal sealed class AppDriver : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private AppDriver(Application app, UIA3Automation automation, Window window)
    {
        App = app;
        Automation = automation;
        Window = window;
    }

    public Application App { get; }
    public UIA3Automation Automation { get; }
    public Window Window { get; }

    public static string Exe
    {
        get
        {
            var path = Path.Combine(Paths.Root, "app", "src", "GpoCatalogue.App", "bin",
                AppContext.BaseDirectory.Contains(@"\Release\") ? "Release" : "Debug", "net10.0-windows", "win-x64", "GpoCatalogue.exe");
            Assert.True(File.Exists(path), "Build the app first: " + path);
            return path;
        }
    }

    public static AppDriver Start(params string[] arguments)
    {
        var info = new ProcessStartInfo(Exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(Exe)! };
        foreach (var a in arguments)
            info.ArgumentList.Add(a);
        return Attach(Application.Launch(info));
    }

    public static AppDriver Attach(Application app)
    {
        var automation = new UIA3Automation();
        try
        {
            var window = app.GetMainWindow(automation, Wait) ?? throw new TimeoutException("The main window did not appear.");
            var driver = new AppDriver(app, automation, window);
            driver.WaitUntil(() => driver.Text("count").Contains("matching"), "the table to load (window: " + window.Title + ")");
            return driver;
        }
        catch
        {
            if (!app.HasExited)
                app.Kill();
            automation.Dispose();
            throw;
        }
    }

    /// <summary>Finds a control by its id, or a tool bar item by its accessible name.</summary>
    public AutomationElement Find(string id, AutomationElement? within = null) =>
        Retry.WhileNull(() => TryFind(id, within), Wait).Result
        ?? throw new InvalidOperationException($"No element with id or name '{id}'.");

    /// <summary>
    /// Walks the tree breadth first without entering data grids: a search through the table would visit
    /// thousands of rows and cells and time out.
    /// </summary>
    public AutomationElement? TryFind(string id, AutomationElement? within = null)
    {
        var queue = new Queue<AutomationElement>([within ?? Window]);
        AutomationElement? byName = null;
        while (queue.Count > 0)
        {
            foreach (var child in queue.Dequeue().FindAllChildren())
            {
                if (child.Properties.AutomationId.ValueOrDefault == id)
                    return child;
                var type = child.Properties.ControlType.ValueOrDefault;
                if (byName is null && child.Properties.Name.ValueOrDefault == id
                    && type is ControlType.Button or ControlType.Edit or ControlType.ComboBox)
                    byName = child;
                if (type is not (ControlType.DataGrid or ControlType.Table or ControlType.Document))
                    queue.Enqueue(child);
            }
        }
        return byName;
    }

    public string Text(string id, AutomationElement? within = null)
    {
        var e = Find(id, within);
        if (e.ControlType is ControlType.Edit or ControlType.Document)
        {
            if (e.Patterns.Text.IsSupported)
                return e.Patterns.Text.Pattern.DocumentRange.GetText(-1);
            if (e.Patterns.Value.IsSupported)
                return e.Patterns.Value.Pattern.Value.Value;
        }
        return e.Name;
    }

    /// <summary>The message at the left of the status bar.</summary>
    public string Status() => StatusPart(0);

    /// <summary>The count of staged changes at the right of the status bar.</summary>
    public string PendingStatus() => StatusPart(1);

    private string StatusPart(int index) =>
        Window.FindFirstChild(cf => cf.ByControlType(ControlType.StatusBar))!
            .FindAllChildren(cf => cf.ByControlType(ControlType.Text))[index].Name;

    public void WaitUntil(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        Exception? last = null;
        var result = Retry.WhileFalse(() =>
        {
            try
            {
                return condition();
            }
            catch (Exception ex)
            {
                // Elements come and go while the window updates.
                last = ex;
                return false;
            }
        }, timeout ?? Wait, TimeSpan.FromMilliseconds(100));
        Assert.True(result.Result, "Timed out waiting for " + what + (last is null ? "" : $" (last error: {last.GetType().Name}: {last.Message})"));
    }

    /// <summary>The number of rows the count line reports.</summary>
    public int Count()
    {
        var text = Text("count");
        return int.Parse(text[..text.IndexOf(' ')]);
    }

    /// <summary>Sets the text of a box, or of the edit field inside a spin box.</summary>
    public void Type(string id, string text, AutomationElement? within = null)
    {
        var e = Find(id, within);
        if (!e.Patterns.Value.IsSupported || e.Patterns.Value.Pattern.IsReadOnly.Value)
            e = e.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit)) ?? e;
        e.Patterns.Value.Pattern.SetValue(text);
    }

    /// <summary>Types into the search box and waits for the table to follow.</summary>
    public void Search(string text, int expected)
    {
        Type("Search all policies", text);
        WaitUntil(() => Count() == expected, $"{expected} rows after searching '{text}'");
    }

    /// <summary>Presses a button, ticks a box, picks a radio button or opens a drop-down.</summary>
    public void Invoke(string id, AutomationElement? within = null) => Act(Find(id, within));

    /// <summary>
    /// Acts on an element. A button that opens a modal dialog does not return from Invoke until the dialog
    /// closes, so the action runs on its own thread and the test carries on after a moment.
    /// </summary>
    public static void Act(AutomationElement e)
    {
        var task = Task.Run(() => ActNow(e));
        if (task.Wait(TimeSpan.FromSeconds(2)) || task.IsFaulted)
            task.GetAwaiter().GetResult();
    }

    private static void ActNow(AutomationElement e)
    {
        var p = e.Patterns;
        if (p.Invoke.IsSupported)
            p.Invoke.Pattern.Invoke();
        else if (p.SelectionItem.IsSupported)
            p.SelectionItem.Pattern.Select();
        else if (p.Toggle.IsSupported)
            p.Toggle.Pattern.Toggle();
        else if (p.ExpandCollapse.IsSupported)
            p.ExpandCollapse.Pattern.Expand();
        else if (p.LegacyIAccessible.IsSupported)
            p.LegacyIAccessible.Pattern.DoDefaultAction();
        else
            throw new InvalidOperationException($"'{e.AutomationId}' ({e.ControlType}) cannot be acted on.");
    }

    /// <summary>Opens a drop-down button of a tool bar and chooses one of its items.</summary>
    public void ChooseMenuItem(string button, string text)
    {
        var owner = Find(button);
        if (owner.Patterns.ExpandCollapse.IsSupported)
            owner.Patterns.ExpandCollapse.Pattern.Expand();
        else
            Act(owner);
        // WinForms lists the items of an open drop-down under its button; other menus are windows of their own.
        var item = Retry.WhileNull(() =>
                owner.FindFirstDescendant(cf => cf.ByControlType(ControlType.MenuItem).And(cf.ByName(text)))
                ?? Automation.GetDesktop().FindAllChildren(cf => cf.ByProcessId(App.ProcessId))
                    .Where(w => w.Properties.NativeWindowHandle.ValueOrDefault != Window.Properties.NativeWindowHandle.Value)
                    .Select(w => w.FindFirstDescendant(cf => cf.ByControlType(ControlType.MenuItem).And(cf.ByName(text))))
                    .FirstOrDefault(m => m is not null), Wait).Result
            ?? throw new InvalidOperationException($"No menu item '{text}' under '{button}'.");
        Act(item);
    }
    public void SelectComboItem(string id, string text, AutomationElement? within = null)
    {
        var combo = Find(id, within);
        if (combo.Patterns.ExpandCollapse.IsSupported)
            combo.Patterns.ExpandCollapse.Pattern.Expand();
        var item = Retry.WhileNull(() => combo.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem).And(cf.ByName(text))), Wait).Result
                   ?? throw new InvalidOperationException($"No item '{text}' in '{id}'.");
        item.Patterns.SelectionItem.Pattern.Select();
        if (combo.Patterns.ExpandCollapse.IsSupported)
            combo.Patterns.ExpandCollapse.Pattern.Collapse();
    }

    /// <summary>Selects every row of the table through accessibility, without the keyboard. Keep the table short.</summary>
    public int SelectAllRows()
    {
        const int TakeFocus = 1, TakeSelection = 2, AddSelection = 8;
        var rows = Find("grid").FindAllChildren().Where(r => r.Name.StartsWith("Row ", StringComparison.Ordinal)).ToList();
        for (var i = 0; i < rows.Count; i++)
            rows[i].Patterns.LegacyIAccessible.Pattern.Select(i == 0 ? TakeFocus | TakeSelection : AddSelection);
        WaitUntil(() => Text("count").Contains($"{rows.Count} selected") || rows.Count < 2, $"{rows.Count} rows to be selected");
        return rows.Count;
    }

    /// <summary>The dialog the program has open on top of the main window.</summary>
    public Window Dialog(string titlePart) =>
        Retry.WhileNull(() => TopLevel().FirstOrDefault(w => w.Name.Contains(titlePart, StringComparison.Ordinal)),
            Wait).Result?.AsWindow() ?? throw new TimeoutException($"No dialog titled *{titlePart}*.");

    /// <summary>The program's windows other than the main one: its dialogs and message boxes.</summary>
    private IEnumerable<AutomationElement> TopLevel()
    {
        // Owned dialogs are children of their owner in the automation tree; others hang off the desktop.
        var main = Window.Properties.NativeWindowHandle.Value;
        var found = Automation.GetDesktop().FindAllChildren(cf => cf.ByProcessId(App.ProcessId))
            .Where(w => w.Properties.NativeWindowHandle.ValueOrDefault != main).ToList();
        var queue = new Queue<AutomationElement>([Window, .. found]);
        while (queue.Count > 0)
        {
            foreach (var child in queue.Dequeue().FindAllChildren(cf => cf.ByControlType(ControlType.Window)))
            {
                found.Add(child);
                queue.Enqueue(child);
            }
        }
        return found;
    }

    /// <summary>Dismisses a message box shown by <paramref name="owner"/> and returns its text.</summary>
    public string AcceptMessage(Window owner, TimeSpan? timeout = null)
    {
        var box = Retry.WhileNull(() => TopLevel().FirstOrDefault(w => w.ClassName == "#32770"), timeout ?? TimeSpan.FromSeconds(60)).Result
                  ?? throw new TimeoutException("No message box.");
        var text = string.Join("\n", box.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Select(t => t.Name));
        Act(box.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("OK")))!);
        return text;
    }

    public string Screenshot(string name, AutomationElement? element = null)
    {
        Directory.CreateDirectory(Paths.Screenshots);
        var path = Path.Combine(Paths.Screenshots, name + ".png");
        (element ?? Window).SetForeground();
        Thread.Sleep(400);
        FlaUI.Core.Capturing.Capture.Element(element ?? Window).ToFile(path);
        return path;
    }

    public void Dispose()
    {
        using var process = Process.GetProcessById(App.ProcessId);
        try
        {
            if (!process.HasExited)
            {
                // Closing normally lets the program save its view, as a person closing it would.
                foreach (var dialog in TopLevel())
                    dialog.AsWindow().Close();
                Window.Close();
            }
        }
        catch (Exception)
        {
            // Killed below.
        }
        if (!process.WaitForExit(5000))
            process.Kill();
        App.Dispose();
        Automation.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}

internal static class Paths
{
    public static string Root
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "build.py")))
                    return dir.FullName;
            }
            throw new DirectoryNotFoundException("Repository root not found.");
        }
    }

    public static string Screenshots => Path.Combine(Root, "app", "tests", "GpoCatalogue.UiTests", "bin", "screenshots");

    public static string NewTemp(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), "GpoCatalogueUiTests", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }
}
