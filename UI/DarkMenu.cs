using System.Drawing;
using System.Windows.Forms;

namespace MonitorDim.UI;

/// <summary>Dark colour table + renderer so the tray menu matches the settings window.</summary>
internal sealed class DarkColorTable : ProfessionalColorTable
{
    public static readonly Color Surface = Color.FromArgb(22, 24, 34);
    public static readonly Color SurfaceHot = Color.FromArgb(35, 39, 54);
    public static readonly Color BorderCol = Color.FromArgb(38, 42, 56);
    public static readonly Color TextCol = Color.FromArgb(233, 236, 245);
    public static readonly Color TextMuted = Color.FromArgb(142, 149, 171);
    public static readonly Color Accent = Color.FromArgb(91, 140, 255);

    public DarkColorTable() => UseSystemColors = false;

    public override Color ToolStripDropDownBackground => Surface;
    public override Color ImageMarginGradientBegin => Surface;
    public override Color ImageMarginGradientMiddle => Surface;
    public override Color ImageMarginGradientEnd => Surface;
    public override Color MenuBorder => BorderCol;
    public override Color MenuItemBorder => Accent;
    public override Color MenuItemSelected => SurfaceHot;
    public override Color MenuItemSelectedGradientBegin => SurfaceHot;
    public override Color MenuItemSelectedGradientEnd => SurfaceHot;
    public override Color MenuItemPressedGradientBegin => SurfaceHot;
    public override Color MenuItemPressedGradientEnd => SurfaceHot;
    public override Color SeparatorDark => BorderCol;
    public override Color SeparatorLight => BorderCol;
    public override Color CheckBackground => Accent;
    public override Color CheckSelectedBackground => Accent;
    public override Color CheckPressedBackground => Accent;
}

internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkColorTable()) { }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? DarkColorTable.TextCol : DarkColorTable.TextMuted;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = DarkColorTable.TextCol;
        base.OnRenderArrow(e);
    }
}
