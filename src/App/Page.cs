using System;
using System.Drawing;
using System.Windows.Forms;

namespace Forja.App
{
    // A page of the main window. Active = selected AND the window is visible (not minimized);
    // pages run timers/threads only while active. While hidden, OnFirmwareTick (the 2 s
    // firmware timer the tray already runs) is the only work a page may do.
    public class Page : Painted
    {
        // Optional small-caps title and a muted line under it (null = none, e.g. Monitor).
        public string Heading;
        public string Subheading;

        public virtual void SetActive(bool active)
        {
        }

        // Called on the UI thread after every firmware tick (every 2 s, window visible or not).
        public virtual void OnFirmwareTick()
        {
        }

        // Called when the window is closing for good (stop threads).
        public virtual void Shutdown()
        {
        }

        // Same 14 px gutter as the instrument panels and the hosted pages.
        protected int Pad { get { return Theme.S(14); } }
        protected int Top0 { get { return Theme.S(Subheading == null ? 44 : 64); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Background);
            if (Heading != null)
            {
                Theme.Draw(g, Heading.ToUpperInvariant(), Theme.Font(9f, true), Theme.Text,
                    new Rectangle(Pad, Theme.S(14), Width - Pad * 2, Theme.S(20)), TextFormatFlags.EndEllipsis);
            }
            if (Subheading != null)
            {
                Theme.Draw(g, Subheading, Theme.Font(9f, false), Theme.Muted,
                    new Rectangle(Pad, Theme.S(36), Width - Pad * 2, Theme.S(18)), TextFormatFlags.EndEllipsis);
            }
            PaintPage(g);
        }

        protected virtual void PaintPage(Graphics g)
        {
        }
    }

    // Hosts one self-contained page control (Modos, Ventoinha, Saúde, Manutenção). Without a
    // Heading the control fills the page (it draws its own title); with one, the page draws
    // the heading like every other page and the control sits below it.
    public class HostPage : Page
    {
        private readonly Control content;
        private readonly Action<bool> onActive;
        private readonly Action<bool> onTick;
        private readonly Action onShutdown;
        private bool active;

        // onTick gets "active" (selected page of a visible window) so hidden work stays light.
        public HostPage(Control content, Action<bool> onActive, Action<bool> onTick, Action onShutdown)
        {
            this.content = content;
            this.onActive = onActive;
            this.onTick = onTick;
            this.onShutdown = onShutdown;
            Controls.Add(content);
        }

        public Control Content { get { return content; } }

        public override void SetActive(bool value)
        {
            active = value;
            if (onActive != null) onActive(value);
        }

        public override void OnFirmwareTick()
        {
            if (onTick != null) onTick(active);
        }

        public override void Shutdown()
        {
            if (onShutdown != null) onShutdown();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int top = Heading == null ? 0 : Top0 - Theme.S(8);
            content.SetBounds(0, top, Width, Math.Max(0, Height - top));
        }
    }
}
