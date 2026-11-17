using System.Drawing;
using System.Windows.Forms;
using Forja.App.Controls;

namespace Forja.App
{
    // Dark tray menu colors (the one palette, through Theme).
    internal sealed class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Theme.Panel; } }
        public override Color MenuBorder { get { return Theme.Border; } }
        public override Color MenuItemBorder { get { return Theme.Hover; } }
        public override Color MenuItemSelected { get { return Theme.Hover; } }
        public override Color MenuItemSelectedGradientBegin { get { return Theme.Hover; } }
        public override Color MenuItemSelectedGradientEnd { get { return Theme.Hover; } }
        public override Color MenuItemPressedGradientBegin { get { return Theme.Selected; } }
        public override Color MenuItemPressedGradientEnd { get { return Theme.Selected; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Panel; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Panel; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Panel; } }
        // check mark square: accent tint with an accent outline (the default is the system blue)
        public override Color ButtonSelectedBorder { get { return Palette.Accent; } }
        public override Color CheckBackground { get { return Palette.AccentBg; } }
        public override Color CheckSelectedBackground { get { return Palette.AccentBg; } }
        public override Color CheckPressedBackground { get { return Palette.AccentBg; } }
        public override Color SeparatorDark { get { return Theme.Border; } }
        public override Color SeparatorLight { get { return Theme.Panel; } }
    }

    internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColors())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Dim;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item != null && e.Item.Enabled ? Theme.Text : Theme.Dim;
            base.OnRenderArrow(e);
        }
    }
}
