using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Forja.Core;

namespace Forja.App
{
    // Tray menu: Abrir · Limpar RAM · Modo ▸ · Ventoinha ▸ · Widget · Sair.
    // Modo / Ventoinha go through the pages' own code paths (MainForm.ChooseMode /
    // ChooseFanAuto / ChooseFanMax), so the one-time Desempenho warning and the Máximo
    // bookkeeping are the same as a click on the page. Actions are posted so the menu has
    // closed before a warning box or a firmware write runs. Until the fan hardware validation
    // passed, Ventoinha offers only Automática plus a disabled note.
    internal sealed class TrayMenu
    {
        private readonly TrayApp app;
        private readonly MainForm form;
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly ToolStripMenuItem miClean;
        // One item per mode the environment test confirmed (ApplyCapabilities rebuilds them).
        private readonly List<ToolStripMenuItem> modeItems = new List<ToolStripMenuItem>();
        private readonly List<PerfMode> modes = new List<PerfMode>();
        private readonly ToolStripMenuItem miMode = new ToolStripMenuItem("Modo");
        private readonly ToolStripMenuItem miFan = new ToolStripMenuItem("Ventoinha");
        private readonly ToolStripMenuItem miFanAuto;
        private readonly ToolStripMenuItem miFanMax;
        private readonly ToolStripMenuItem miFanPage;
        private readonly ToolStripMenuItem miWidget;

        public TrayMenu(TrayApp app, MainForm form)
        {
            this.app = app;
            this.form = form;
            menu.Renderer = new DarkMenuRenderer();
            menu.ShowItemToolTips = true;

            ToolStripMenuItem miOpen = new ToolStripMenuItem("Abrir " + Display.AppName, null, delegate { app.ShowMainWindow(); });
            miOpen.Font = new Font(menu.Font, FontStyle.Bold);
            miClean = new ToolStripMenuItem("Limpar RAM", null, delegate { app.CleanRam(true); });
            if (app.ReadOnly)
            {
                miClean.Enabled = false;
                miClean.ToolTipText = SafetyText.ReadOnlyShort;
            }

            Style(miMode);
            Style(miFan);
            miFanAuto = new ToolStripMenuItem("Automática", null, delegate { Post(form.ChooseFanAuto); });
            miFanMax = new ToolStripMenuItem("Máximo", null, delegate { Post(form.ChooseFanMax); });
            miFanPage = new ToolStripMenuItem("Manual ou curva…", null, delegate { Post(delegate { app.ShowPage("Ventoinha"); }); });
            miWidget = new ToolStripMenuItem("Mostrar o widget", null, delegate { Post(app.ToggleWidget); });
            ToolStripMenuItem miExit = new ToolStripMenuItem("Sair", null, delegate { Post(app.RequestQuit); });
            menu.Items.AddRange(new ToolStripItem[]
            {
                miOpen, new ToolStripSeparator(), miClean, miMode, miFan, miWidget, new ToolStripSeparator(), miExit
            });
            ApplyCapabilities();
            menu.Opening += delegate { RefreshChecks(); };
            app.CleaningChanged += delegate
            {
                miClean.Enabled = !app.Cleaning && !app.ReadOnly;
                miClean.Text = app.Cleaning ? "Limpando RAM…" : "Limpar RAM";
            };
        }

        public ContextMenuStrip Menu { get { return menu; } }

        // The Modo and Ventoinha submenus follow the environment profile: only confirmed modes, and the fan
        // choices Manual/Curva/Máximo only after the fan test. Called at start and after the wizard.
        public void ApplyCapabilities()
        {
            miMode.DropDownItems.Clear();
            modeItems.Clear();
            modes.Clear();
            List<PerfMode> offered = app.OfferedModes();
            foreach (PerfMode mode in offered)
            {
                PerfMode m = mode;
                ToolStripMenuItem it = new ToolStripMenuItem(ModeCatalog.CardName(m), null, delegate { Post(delegate { form.ChooseMode(m); }); });
                if (app.ReadOnly)
                {
                    it.Enabled = false;
                    it.ToolTipText = SafetyText.ReadOnlyShort;
                }
                modes.Add(m);
                modeItems.Add(it);
                miMode.DropDownItems.Add(it);
            }
            if (offered.Count == 0)
            {
                ToolStripMenuItem none = new ToolStripMenuItem(app.ModesNote ?? "Faça o teste de ambiente para liberar os modos");
                none.Enabled = false;
                miMode.DropDownItems.Add(none);
            }

            miFan.DropDownItems.Clear();
            if (form.FanManualAllowed)
            {
                miFan.DropDownItems.AddRange(new ToolStripItem[] { miFanAuto, miFanMax, new ToolStripSeparator(), miFanPage });
            }
            else
            {
                ToolStripMenuItem locked = new ToolStripMenuItem(app.FanLockedNote);
                locked.Enabled = false;
                miFan.DropDownItems.AddRange(new ToolStripItem[] { miFanAuto, new ToolStripSeparator(), locked });
            }
        }

        private static void Style(ToolStripMenuItem parent)
        {
            ToolStripDropDownMenu dd = parent.DropDown as ToolStripDropDownMenu;
            if (dd != null)
            {
                dd.Renderer = new DarkMenuRenderer();
                dd.ShowImageMargin = false;
                dd.ShowCheckMargin = true;
            }
        }

        // UI thread (menu opening): one firmware mode read, the rest is cached state.
        private void RefreshChecks()
        {
            PerfMode? cur = app.ReadMode();
            for (int i = 0; i < modeItems.Count; i++)
            {
                modeItems[i].Checked = cur.HasValue && cur.Value == modes[i];
                modeItems[i].Enabled = !app.ReadOnly && !app.EnvOwnsFirmware; // the wizard may own the firmware right now
            }
            string fan = form.FanModeLabel;
            miFanAuto.Checked = fan == "Automática";
            miFanMax.Checked = fan == "Máximo";
            miFanMax.Enabled = !app.EnvOwnsFirmware; // the wizard's fan test owns the fans right now
            miFanAuto.Enabled = !app.EnvOwnsFirmware;
            miFanPage.Checked = fan == "Manual" || fan == "Curva";
            miWidget.Text = app.WidgetWanted ? "Esconder o widget" : "Mostrar o widget";
        }

        private void Post(Action a)
        {
            SynchronizationPost(a);
        }

        private static void SynchronizationPost(Action a)
        {
            System.Threading.SynchronizationContext ctx = System.Threading.SynchronizationContext.Current;
            if (ctx == null) { a(); return; }
            ctx.Post(delegate
            {
                try { a(); }
                catch (Exception ex) { Program.LogError(ex); }
            }, null);
        }
    }
}
