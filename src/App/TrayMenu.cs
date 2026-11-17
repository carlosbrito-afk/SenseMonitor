using System;
using System.Drawing;
using System.Windows.Forms;
using Forja.Core;

namespace Forja.App
{
    // Tray menu: Abrir · Limpar RAM · Modo ▸ · Ventoinha ▸ · Sair.
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
        private readonly ToolStripMenuItem[] modeItems = new ToolStripMenuItem[3];
        private readonly PerfMode[] modes = { PerfMode.Silencioso, PerfMode.DiaADia, PerfMode.Desempenho };
        private readonly ToolStripMenuItem miFanAuto;
        private readonly ToolStripMenuItem miFanMax;
        private readonly ToolStripMenuItem miFanPage;

        public TrayMenu(TrayApp app, MainForm form)
        {
            this.app = app;
            this.form = form;
            menu.Renderer = new DarkMenuRenderer();
            menu.ShowItemToolTips = true;

            ToolStripMenuItem miOpen = new ToolStripMenuItem("Abrir " + Display.AppName, null, delegate { app.ShowMainWindow(); });
            miOpen.Font = new Font(menu.Font, FontStyle.Bold);
            miClean = new ToolStripMenuItem("Limpar RAM", null, delegate { app.CleanRam(true); });

            ToolStripMenuItem miMode = new ToolStripMenuItem("Modo");
            string[] names = { "Silencioso", "Dia a dia", "Desempenho (Gaming)" };
            for (int i = 0; i < 3; i++)
            {
                PerfMode m = modes[i];
                modeItems[i] = new ToolStripMenuItem(names[i], null, delegate { Post(delegate { form.ChooseMode(m); }); });
                miMode.DropDownItems.Add(modeItems[i]);
            }
            Style(miMode);

            ToolStripMenuItem miFan = new ToolStripMenuItem("Ventoinha");
            miFanAuto = new ToolStripMenuItem("Automática", null, delegate { Post(form.ChooseFanAuto); });
            miFanMax = new ToolStripMenuItem("Máximo", null, delegate { Post(form.ChooseFanMax); });
            miFanPage = new ToolStripMenuItem("Manual ou curva…", null, delegate { Post(delegate { app.ShowPage("Ventoinha"); }); });
            if (form.FanManualAllowed)
            {
                miFan.DropDownItems.AddRange(new ToolStripItem[] { miFanAuto, miFanMax, new ToolStripSeparator(), miFanPage });
            }
            else
            {
                ToolStripMenuItem locked = new ToolStripMenuItem(FanValidation.LockedNote);
                locked.Enabled = false;
                miFan.DropDownItems.AddRange(new ToolStripItem[] { miFanAuto, new ToolStripSeparator(), locked });
            }
            Style(miFan);

            ToolStripMenuItem miExit = new ToolStripMenuItem("Sair", null, delegate { Post(app.RequestQuit); });
            menu.Items.AddRange(new ToolStripItem[]
            {
                miOpen, new ToolStripSeparator(), miClean, miMode, miFan, new ToolStripSeparator(), miExit
            });
            menu.Opening += delegate { RefreshChecks(); };
            app.CleaningChanged += delegate
            {
                miClean.Enabled = !app.Cleaning;
                miClean.Text = app.Cleaning ? "Limpando RAM…" : "Limpar RAM";
            };
        }

        public ContextMenuStrip Menu { get { return menu; } }

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
            for (int i = 0; i < 3; i++)
            {
                modeItems[i].Checked = cur.HasValue && cur.Value == modes[i];
            }
            string fan = form.FanModeLabel;
            miFanAuto.Checked = fan == "Automática";
            miFanMax.Checked = fan == "Máximo";
            miFanPage.Checked = fan == "Manual" || fan == "Curva";
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
