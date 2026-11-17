using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using Forja.Core;

namespace Forja.App.Controls
{
    // Top strip from mockup D: LED + one of two words ("Estável" / "Requer sua atenção"), an optional
    // detail text, and telemetry pairs on the right ("MODO Dia a dia", "VENTOINHA Automática").
    // The state comes from the alert engine (sustained heat, thermal protection, sensor lost, fan
    // safety event, restart not yet seen), so it can never say "Estável" while a sensor is unavailable.
    // Clicking it while it asks for attention raises Clicked (the window opens the page that explains why).
    // When space runs out the detail is shortened first, then telemetry pairs are dropped from the
    // left; the word itself is never cut. 46 px tall at 100 %, bottom border included.
    public class VerdictStrip : InstrumentControl
    {
        public const int LogicalHeight = 46;

        private StripState _state = StripState.Reading;
        private bool _grace = true;
        private bool _critical;
        private string _reason;
        private string _detail = "";
        private readonly List<KeyValuePair<string, string>> _telemetry = new List<KeyValuePair<string, string>>();

        public VerdictStrip()
        {
            BackColor = Palette.Bg;
            UpdateAccessibleText();
        }

        protected override Size DefaultSize
        {
            get { return new Size(684, LogicalHeight); }
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            return new Size(Math.Max(proposedSize.Width, PxI(300)), PxI(LogicalHeight));
        }

        public Verdict Verdict
        {
            get
            {
                if (_state == StripState.Reading) return Verdict.NoReading;   // neutral LED
                return _state == StripState.Stable ? Verdict.Stable : (_critical ? Verdict.Hot : Verdict.Attention);
            }
        }

        public bool NeedsAttention
        {
            get { return _state == StripState.Attention; }
        }

        // Raised when the user clicks the strip while it asks for attention.
        public event Action Clicked;

        // critical: protection engaged (red LED). reason: the tooltip, null when stable.
        public void SetState(StripState state, bool critical, string reason, bool readingGrace)
        {
            bool needs = state == StripState.Attention;
            critical = needs && critical;
            string why = needs ? reason : null;
            if (state == _state && critical == _critical && why == _reason && readingGrace == _grace) return;
            _state = state;
            _grace = readingGrace;
            _critical = critical;
            _reason = why;
            Cursor = needs ? System.Windows.Forms.Cursors.Hand : System.Windows.Forms.Cursors.Default;
            UpdateAccessibleText();
            Invalidate();
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            Action h = Clicked;
            if (_state == StripState.Attention && h != null) h();
        }

        // Optional sentence after the word (the read-only banner); never crash or restart text.
        public string Detail
        {
            get { return _detail; }
            set
            {
                value = value ?? "";
                if (value == _detail) return;
                _detail = value;
                UpdateAccessibleText();
                Invalidate();
            }
        }

        // Alternating key, value: SetTelemetry("Modo", "Dia a dia", "Ventoinha", "Automática").
        public void SetTelemetry(params string[] keyValuePairs)
        {
            List<KeyValuePair<string, string>> next = new List<KeyValuePair<string, string>>();
            if (keyValuePairs != null)
            {
                for (int i = 0; i + 1 < keyValuePairs.Length; i += 2)
                {
                    next.Add(new KeyValuePair<string, string>(keyValuePairs[i] ?? "", keyValuePairs[i + 1] ?? ""));
                }
            }
            bool same = next.Count == _telemetry.Count;
            for (int i = 0; same && i < next.Count; i++)
            {
                same = next[i].Key == _telemetry[i].Key && next[i].Value == _telemetry[i].Value;
            }
            if (same) return;
            _telemetry.Clear();
            _telemetry.AddRange(next);
            UpdateAccessibleText();
            Invalidate();
        }

        private void UpdateAccessibleText()
        {
            string word = StripText.Label(_state, _grace);
            AccessibleName = "Estado: " + (word.Length > 0 ? word : "ainda verificando");
            StringBuilder sb = new StringBuilder(_state == StripState.Attention && _reason != null ? _reason : "");
            if (_detail.Length > 0)
            {
                if (sb.Length > 0) sb.Append("; ");
                sb.Append(_detail);
            }
            foreach (KeyValuePair<string, string> p in _telemetry)
            {
                if (sb.Length > 0) sb.Append("; ");
                sb.Append(p.Key).Append(' ').Append(p.Value);
            }
            AccessibleDescription = sb.ToString();
        }

        protected override void PrepareResources()
        {
            Ink.Prepare(UiSemibold(15f));
            Ink.Prepare(Ui(13f));
            Ink.Prepare(Ui(10.5f));
        }

        protected override void OnPaint(System.Windows.Forms.PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            int W = ClientSize.Width;
            int H = ClientSize.Height;
            int hair = Hairline;
            g.FillRectangle(Res.Brush(Palette.Line), 0, H - hair, W, hair);

            Font state = UiSemibold(15f);
            Font body = Ui(13f);
            Font key = Ui(10.5f);
            float track = 10.5f * 0.09f * Factor;
            float mid = (H - hair) / 2f;
            float baseline = (float)Math.Round(mid + Ink.CapHeight(state) / 2f);

            int pad = PxI(16f);
            int gap = PxI(18f);
            float x = pad;

            // LED + verdict word (never shortened)
            float led = Px(8f);
            Smooth(g);
            g.FillEllipse(Res.Brush(Palette.ForVerdict(Verdict)), x, mid - led / 2f, led, led);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Default;
            x += led + Px(8f);
            string word = StripText.Label(_state, _grace);
            if (word.Length > 0) x += Ink.Draw(g, word, state, _state == StripState.Reading ? Palette.Sec : Palette.Text, x, baseline, InkAlign.Left);

            // telemetry: measure, then drop pairs from the left until the detail keeps some room
            int pairGap = PxI(6f);
            int[] widths = new int[_telemetry.Count];
            string[] keys = new string[_telemetry.Count];
            for (int i = 0; i < _telemetry.Count; i++)
            {
                keys[i] = _telemetry[i].Key.ToUpperInvariant();
                widths[i] = Ink.TrackedWidth(keys[i], key, track) + pairGap + Ink.Width(_telemetry[i].Value, body);
            }
            int first = 0;
            int teleW = TelemetryWidth(widths, first, gap);
            int minDetail = _detail.Length > 0 ? Math.Min(Ink.Width(_detail, body), PxI(80f)) : 0;
            while (first < widths.Length && x + gap + minDetail + (teleW > 0 ? gap + teleW : 0) > W - pad)
            {
                first++;
                teleW = TelemetryWidth(widths, first, gap);
            }

            float right = W - pad;
            if (teleW > 0)
            {
                float tx = right - teleW;
                for (int i = first; i < widths.Length; i++)
                {
                    int kw = Ink.DrawTracked(g, keys[i], key, Palette.Mute, tx, baseline, track, InkAlign.Left);
                    Ink.Draw(g, _telemetry[i].Value, body, Palette.Text, tx + kw + pairGap, baseline, InkAlign.Left);
                    tx += widths[i] + gap;
                }
                right -= teleW + gap;
            }

            if (_detail.Length > 0)
            {
                float dx = x + gap;
                int room = (int)Math.Floor(right - dx);
                if (room > 0)
                {
                    Ink.Draw(g, Ink.Fit(_detail, body, room), body, Palette.Sec, dx, baseline, InkAlign.Left);
                }
            }
        }

        private static int TelemetryWidth(int[] widths, int first, int gap)
        {
            int w = 0;
            for (int i = first; i < widths.Length; i++)
            {
                if (w > 0) w += gap;
                w += widths[i];
            }
            return w;
        }
    }
}
