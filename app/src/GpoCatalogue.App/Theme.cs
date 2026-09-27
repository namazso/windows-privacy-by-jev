namespace GpoCatalogue.App;

/// <summary>The website's palette (site/styles.css), in a light and a dark variant.</summary>
public sealed class Theme
{
    public static Theme Current { get; private set; } = Light();

    public required bool IsDark { get; init; }
    public required Color Bg { get; init; }
    public required Color Fg { get; init; }
    public required Color Muted { get; init; }
    public required Color Line { get; init; }
    public required Color Head { get; init; }
    public required Color RowAlt { get; init; }
    public required Color Hover { get; init; }
    public required Color Accent { get; init; }
    public required Color Selection { get; init; }
    public required Color LowBg { get; init; }
    public required Color LowFg { get; init; }
    public required Color MidBg { get; init; }
    public required Color MidFg { get; init; }
    public required Color HighBg { get; init; }
    public required Color HighFg { get; init; }
    public required Color FlagBg { get; init; }
    public required Color FlagFg { get; init; }

    public Font Body { get; } = new("Segoe UI", 9f);
    public Font Bold { get; } = new("Segoe UI", 9f, FontStyle.Bold);
    public Font Small { get; } = new("Segoe UI", 7.75f);
    public Font Title { get; } = new("Segoe UI Semibold", 12f);
    public Font Heading { get; } = new("Segoe UI Semibold", 10f);
    public Font Mono { get; } = new("Consolas", 8.5f);

    public static void Use(bool dark) => Current = dark ? Dark() : Light();

    private static Color Hex(string hex) => ColorTranslator.FromHtml(hex);

    private static Theme Light() => new()
    {
        IsDark = false,
        Bg = Hex("#ffffff"), Fg = Hex("#1c1e21"), Muted = Hex("#5f6368"), Line = Hex("#d9dce1"),
        Head = Hex("#f3f4f6"), RowAlt = Hex("#fafbfc"), Hover = Hex("#eef3ff"), Accent = Hex("#1a56db"),
        Selection = Hex("#d6e2ff"),
        LowBg = Hex("#eef0f2"), LowFg = Hex("#4b5563"), MidBg = Hex("#fff3cd"), MidFg = Hex("#7a5a00"),
        HighBg = Hex("#d9f2e2"), HighFg = Hex("#0f5e34"), FlagBg = Hex("#ffe4e6"), FlagFg = Hex("#9f1239"),
    };

    private static Theme Dark() => new()
    {
        IsDark = true,
        Bg = Hex("#141517"), Fg = Hex("#e6e7ea"), Muted = Hex("#a0a4ab"), Line = Hex("#33363c"),
        Head = Hex("#1e2023"), RowAlt = Hex("#18191c"), Hover = Hex("#22304a"), Accent = Hex("#7fa7ff"),
        Selection = Hex("#2b3f66"),
        LowBg = Hex("#24262a"), LowFg = Hex("#b3b7be"), MidBg = Hex("#4a3a05"), MidFg = Hex("#ffd866"),
        HighBg = Hex("#0f3b26"), HighFg = Hex("#86e0ab"), FlagBg = Hex("#4a1220"), FlagFg = Hex("#ff9fb2"),
    };

    /// <summary>Colours a control tree the standard colour mode leaves alone.</summary>
    public void Apply(Control root)
    {
        foreach (Control c in root.Controls)
            Apply(c);
        switch (root)
        {
            case Form or Panel or TableLayoutPanel or FlowLayoutPanel or SplitContainer or SplitterPanel or GroupBox
                or Label or CheckBox or RadioButton:
                root.BackColor = root.Tag as string == "head" ? Head : Bg;
                root.ForeColor = Fg;
                break;
            case TextBoxBase or ComboBox or NumericUpDown or ListBox:
                root.BackColor = Bg;
                root.ForeColor = Fg;
                break;
        }
    }
}
