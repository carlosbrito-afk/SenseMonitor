using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;

namespace Forja.App.Controls
{
    // Text block beside a gauge (mockup D): spaced title ("CPU") with a live marker ("• 1 s"),
    // then rows "KEY ........ 2.961 rpm" separated by 1 px lines. Values are never cut;
    // keys are shortened with an ellipsis if the row is too narrow. Content is centered vertically.
    public class InstrumentReadout : InstrumentControl
    {
        private sealed class Row
        {
            public string Key = "";
            public string Value = "";
            public string Unit = "";
        }

        private const float HeaderH = 16f;
        private const float HeaderGap = 8f;
        private const float RowH = 24f;

        private readonly List<Row> _rows = new List<Row>();
        private string _title = "CPU";
        private string _liveText = "1 s";

        public InstrumentReadout()
        {
            UpdateAccessibleText();
        }

        protected override Size DefaultSize
        {
            get { return new Size(160, 132); }
        }

        // Height needed for the current rows, at the current DPI.
        public override Size GetPreferredSize(Size proposedSize)
        {
            return new Size(Math.Max(proposedSize.Width, PxI(150)), PxI(HeaderH + HeaderGap + RowH * Math.Max(1, _rows.Count)));
        }

        public string Title
        {
            get { return _title; }
            set
            {
                value = value ?? "";
                if (value == _title) return;
                _title = value;
                UpdateAccessibleText();
                Invalidate();
            }
        }

        // Small mono text after the accent dot ("1 s"); null or "" hides the marker.
        public string LiveText
        {
            get { return _liveText; }
            set
            {
                value = value ?? "";
                if (value == _liveText) return;
                _liveText = value;
                Invalidate();
            }
        }

        public int RowCount
        {
            get { return _rows.Count; }
        }

        // key is shown uppercase and letter-spaced; value in mono; unit small mono after it.
        public void SetRow(int index, string key, string value, string unit)
        {
            if (index < 0) throw new ArgumentOutOfRangeException("index");
            while (_rows.Count <= index) _rows.Add(new Row());
            Row r = _rows[index];
            key = key ?? "";
            value = value ?? "";
            unit = unit ?? "";
            if (r.Key == key && r.Value == value && r.Unit == unit) return;
            r.Key = key;
            r.Value = value;
            r.Unit = unit;
            UpdateAccessibleText();
            Invalidate();
        }

        public void ClearRows()
        {
            if (_rows.Count == 0) return;
            _rows.Clear();
            UpdateAccessibleText();
            Invalidate();
        }

        private void UpdateAccessibleText()
        {
            AccessibleName = _title;
            StringBuilder sb = new StringBuilder();
            foreach (Row r in _rows)
            {
                if (sb.Length > 0) sb.Append("; ");
                sb.Append(r.Key).Append(' ').Append(r.Value);
                if (r.Unit.Length > 0) sb.Append(' ').Append(r.Unit);
            }
            AccessibleDescription = sb.ToString();
        }

        protected override void PrepareResources()
        {
            Ink.Prepare(UiSemibold(12f));
            Ink.Prepare(Mono(11f));
            Ink.Prepare(Ui(10.5f));
            Ink.Prepare(Mono(14f));
        }

        protected override void OnPaint(System.Windows.Forms.PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            int W = ClientSize.Width;
            float block = Px(HeaderH + HeaderGap + RowH * _rows.Count);
            float top = (float)Math.Floor((ClientSize.Height - block) / 2f);
            if (top < 0) top = 0;

            Font title = UiSemibold(12f);
            Font live = Mono(11f);
            Font key = Ui(10.5f);
            Font num = Mono(14f);
            Font unit = Mono(11f);

            // header
            float hb = top + Px(HeaderH) / 2f + Ink.CapHeight(title) / 2f;
            int liveW = 0;
            if (_liveText.Length > 0)
            {
                liveW = Ink.Width(_liveText, live);
                Ink.Draw(g, _liveText, live, Palette.Mute, W, hb, InkAlign.Right);
                float d = Px(6f);
                float dx = W - liveW - Px(6f) - d;
                float dy = top + Px(HeaderH) / 2f - d / 2f;
                Smooth(g);
                g.FillEllipse(Res.Brush(Palette.Accent), dx, dy, d, d);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Default;
                // dot gap 6 + dot + header gap 8
                liveW += PxI(14f) + (int)Math.Ceiling(d);
            }
            float titleTrack = 12f * 0.12f * Factor;
            string t = Ink.FitTracked(_title, title, titleTrack, W - liveW);
            Ink.DrawTracked(g, t, title, Palette.Text, 0, hb, titleTrack, InkAlign.Left);

            // rows
            float y = top + Px(HeaderH + HeaderGap);
            float keyTrack = 10.5f * 0.09f * Factor;
            int gap = PxI(10f);
            int unitGap = PxI(3f);
            foreach (Row r in _rows)
            {
                g.FillRectangle(Res.Brush(Palette.Line), 0, (int)Math.Round(y), W, Hairline);
                float rowTop = y + Hairline;
                float rowH = Px(RowH) - Hairline;
                float baseline = (float)Math.Round(rowTop + rowH / 2f + Ink.CapHeight(num) / 2f);

                int unitW = r.Unit.Length > 0 ? Ink.Width(r.Unit, unit) : 0;
                int valueW = Ink.Width(r.Value, num);
                int right = W;
                if (unitW > 0)
                {
                    Ink.Draw(g, r.Unit, unit, Palette.Mute, right, baseline, InkAlign.Right);
                    right -= unitW + unitGap;
                }
                Ink.Draw(g, r.Value, num, Palette.Text, right, baseline, InkAlign.Right);
                right -= valueW + gap;

                string k = Ink.FitTracked(r.Key.ToUpperInvariant(), key, keyTrack, Math.Max(0, right));
                Ink.DrawTracked(g, k, key, Palette.Mute, 0, baseline, keyTrack, InkAlign.Left);
                y += Px(RowH);
            }
        }
    }
}
