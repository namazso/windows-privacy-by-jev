using System.Runtime.InteropServices;

namespace GpoCatalogue.App;

/// <summary>Builds formatted, read-only text in a RichTextBox.</summary>
internal sealed class RichText(RichTextBox box)
{
    private const int WM_SETREDRAW = 0x000B;

    public static RichTextBox Create(string name, string id) => new()
    {
        ReadOnly = true,
        BorderStyle = BorderStyle.None,
        DetectUrls = false,
        ScrollBars = RichTextBoxScrollBars.Vertical,
        Dock = DockStyle.Fill,
        Name = id,
        AccessibleName = name,
        TabStop = true,
        HideSelection = true,
    };

    public void Render(Action<RichText> build)
    {
        var t = Theme.Current;
        box.BackColor = t.Head;
        SendMessage(box.Handle, WM_SETREDRAW, 0, 0);
        try
        {
            box.Clear();
            build(this);
            box.SelectionStart = 0;
            box.SelectionLength = 0;
            box.ScrollToCaret();
        }
        finally
        {
            SendMessage(box.Handle, WM_SETREDRAW, 1, 0);
            box.Invalidate();
        }
    }

    public RichText Add(string text, Font? font = null, Color? color = null, int indent = 0, Color? back = null)
    {
        var t = Theme.Current;
        box.SelectionStart = box.TextLength;
        box.SelectionLength = 0;
        box.SelectionFont = font ?? t.Body;
        box.SelectionColor = color ?? t.Fg;
        box.SelectionBackColor = back ?? t.Head;
        box.SelectionIndent = indent;
        box.SelectionHangingIndent = 0;
        box.AppendText(text);
        return this;
    }

    public RichText Line(string text = "", Font? font = null, Color? color = null, int indent = 0) =>
        Add(text + "\n", font, color, indent);

    public RichText Title(string text) => Line(text, Theme.Current.Title).Gap();

    public RichText Heading(string text) => Line(text, Theme.Current.Heading, Theme.Current.Fg);

    public RichText Muted(string text, int indent = 0) => Line(text, Theme.Current.Body, Theme.Current.Muted, indent);

    public RichText Mono(string text, int indent = 0) => Line(text, Theme.Current.Mono, Theme.Current.Muted, indent);

    public RichText Field(string label, string value)
    {
        Add(label + ":  ", Theme.Current.Body, Theme.Current.Muted);
        return Line(value);
    }

    public RichText Gap() => Line("", new Font("Segoe UI", 4f));

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
}
