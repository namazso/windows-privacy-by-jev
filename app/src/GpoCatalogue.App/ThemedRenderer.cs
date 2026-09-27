namespace GpoCatalogue.App;

/// <summary>Draws tool strips and menus in the colours of the theme.</summary>
internal sealed class ThemedRenderer : ToolStripProfessionalRenderer
{
    public ThemedRenderer() : base(new Colors()) => RoundedEdges = false;

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.Current.Fg : Theme.Current.Muted;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Theme.Current.Fg;
        base.OnRenderArrow(e);
    }

    private sealed class Colors : ProfessionalColorTable
    {
        private static Theme T => Theme.Current;
        public override Color ToolStripGradientBegin => T.Head;
        public override Color ToolStripGradientMiddle => T.Head;
        public override Color ToolStripGradientEnd => T.Head;
        public override Color ToolStripBorder => T.Line;
        public override Color ToolStripDropDownBackground => T.Bg;
        public override Color StatusStripGradientBegin => T.Head;
        public override Color StatusStripGradientEnd => T.Head;
        public override Color MenuBorder => T.Line;
        public override Color MenuItemBorder => T.Accent;
        public override Color MenuItemSelected => T.Hover;
        public override Color MenuItemSelectedGradientBegin => T.Hover;
        public override Color MenuItemSelectedGradientEnd => T.Hover;
        public override Color MenuItemPressedGradientBegin => T.Selection;
        public override Color MenuItemPressedGradientEnd => T.Selection;
        public override Color ImageMarginGradientBegin => T.Bg;
        public override Color ImageMarginGradientMiddle => T.Bg;
        public override Color ImageMarginGradientEnd => T.Bg;
        public override Color ButtonSelectedHighlight => T.Hover;
        public override Color ButtonSelectedGradientBegin => T.Hover;
        public override Color ButtonSelectedGradientMiddle => T.Hover;
        public override Color ButtonSelectedGradientEnd => T.Hover;
        public override Color ButtonSelectedBorder => T.Accent;
        public override Color ButtonPressedGradientBegin => T.Selection;
        public override Color ButtonPressedGradientMiddle => T.Selection;
        public override Color ButtonPressedGradientEnd => T.Selection;
        public override Color ButtonCheckedGradientBegin => T.Selection;
        public override Color ButtonCheckedGradientMiddle => T.Selection;
        public override Color ButtonCheckedGradientEnd => T.Selection;
        public override Color SeparatorDark => T.Line;
        public override Color SeparatorLight => T.Head;
        public override Color CheckBackground => T.Selection;
        public override Color CheckSelectedBackground => T.Selection;
        public override Color CheckPressedBackground => T.Selection;
    }
}
