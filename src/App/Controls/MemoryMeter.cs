using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using Forja.Core;

namespace Forja.App.Controls
{
    // Left part of the memory panel in mockup D: "MEMÓRIA" over "12,4 / 31,7 GB", then a 4 px usage
    // bar with "EM USO ... 39%". The number block keeps its natural width; the bar takes the rest
    // and is hidden when there is no room. The "Limpar RAM" button and its result text stay outside.
    public class MemoryMeter : InstrumentControl
    {
        public const int LogicalHeight = 42;

        private double? _used;
        private double? _total;

        public MemoryMeter()
        {
            UpdateAccessibleText();
        }

        protected override Size DefaultSize
        {
            get { return new Size(318, LogicalHeight); }
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            return new Size(Math.Max(proposedSize.Width, PxI(200)), PxI(LogicalHeight));
        }

        public double? UsedGb
        {
            get { return _used; }
        }

        public double? TotalGb
        {
            get { return _total; }
        }

        // GB (any unit, as long as both match); null or non-positive total = unavailable.
        public void SetMemory(double? usedGb, double? totalGb)
        {
            if (!Valid(totalGb) || !usedGb.HasValue || double.IsNaN(usedGb.Value) || usedGb.Value < 0)
            {
                usedGb = null;
                totalGb = Valid(totalGb) ? totalGb : null;
            }
            if (Nullable.Equals(usedGb, _used) && Nullable.Equals(totalGb, _total)) return;
            _used = usedGb;
            _total = totalGb;
            UpdateAccessibleText();
            Invalidate();
        }

        private static bool Valid(double? v)
        {
            return v.HasValue && !double.IsNaN(v.Value) && !double.IsInfinity(v.Value) && v.Value > 0;
        }

        private int? Percent
        {
            get
            {
                if (!_used.HasValue || !_total.HasValue) return null;
                double p = Math.Max(0.0, Math.Min(1.0, _used.Value / _total.Value)) * 100.0;
                return (int)Math.Round(p, MidpointRounding.AwayFromZero);
            }
        }

        private string UsedText
        {
            get { return _used.HasValue ? InstrumentMath.FormatDecimal1(_used.Value) : InstrumentMath.Dash; }
        }

        private string TotalText
        {
            get
            {
                string total = _total.HasValue ? InstrumentMath.FormatDecimal1(_total.Value) : InstrumentMath.Dash;
                return InstrumentMath.Nbsp + "/" + InstrumentMath.Nbsp + InstrumentMath.WithUnit(total, "GB");
            }
        }

        private void UpdateAccessibleText()
        {
            AccessibleName = "Memória";
            int? p = Percent;
            AccessibleDescription = p.HasValue
                ? UsedText + " de " + InstrumentMath.WithUnit(InstrumentMath.FormatDecimal1(_total.Value), "GB") + " em uso (" + p.Value + "%)"
                : "sem leitura";
        }

        protected override void PrepareResources()
        {
            Ink.Prepare(Ui(10.5f));
            Ink.Prepare(Mono(20f));
            Ink.Prepare(Mono(13f));
            Ink.Prepare(Mono(12f));
        }

        protected override void OnPaint(System.Windows.Forms.PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            int W = ClientSize.Width;
            float top = (float)Math.Floor((ClientSize.Height - Px(LogicalHeight)) / 2f);
            if (top < 0) top = 0;

            Font key = Ui(10.5f);
            Font big = Mono(20f);
            Font bigUnit = Mono(13f);
            Font pct = Mono(12f);
            float track = 10.5f * 0.09f * Factor;

            // number block
            float kBase = (float)Math.Round(top + Px(7f) + Ink.CapHeight(key) / 2f);
            float bBase = (float)Math.Round(top + Px(29f) + Ink.CapHeight(big) / 2f);
            int kw = Ink.DrawTracked(g, "MEMÓRIA", key, Palette.Mute, 0, kBase, track, InkAlign.Left);
            int bw = Ink.DrawGray(g, UsedText, big, Palette.Text, 0, bBase, InkAlign.Left);
            bw += Ink.Draw(g, TotalText, bigUnit, Palette.Mute, bw, bBase, InkAlign.Left);
            int blockW = Math.Max(kw, bw);

            // bar block
            float bx = blockW + Px(18f);
            float barW = W - bx;
            int? p = Percent;
            string pctText = p.HasValue ? p.Value + "%" : InstrumentMath.Dash;
            int pw = Ink.Width(pctText, pct);
            int capW = Ink.TrackedWidth("EM USO", key, track);
            if (barW < capW + pw + Px(12f)) return;

            float barTop = top + Px(9f);
            float barH = Px(4f);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath track1 = Pill(bx, barTop, barW, barH))
            {
                g.FillPath(Res.Brush(Palette.Line2), track1);
            }
            if (p.HasValue && p.Value > 0)
            {
                float fw = Math.Max(barH, barW * p.Value / 100f);
                using (GraphicsPath fill = Pill(bx, barTop, fw, barH))
                {
                    g.FillPath(Res.Brush(Palette.Sec), fill);
                }
            }
            g.SmoothingMode = SmoothingMode.None;

            float lBase = (float)Math.Round(top + Px(26f) + Ink.CapHeight(key) / 2f);
            Ink.DrawTracked(g, "EM USO", key, Palette.Mute, bx, lBase, track, InkAlign.Left);
            Ink.Draw(g, pctText, pct, Palette.Sec, W, lBase, InkAlign.Right);
        }

        private static GraphicsPath Pill(float x, float y, float w, float h)
        {
            GraphicsPath p = new GraphicsPath();
            p.AddArc(x, y, h, h, 90, 180);
            p.AddArc(x + w - h, y, h, h, 270, 180);
            p.CloseFigure();
            return p;
        }
    }
}
