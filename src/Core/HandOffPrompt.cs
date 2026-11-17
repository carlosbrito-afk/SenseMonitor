using System;

namespace Forja.Core
{
    // What the hand-off question says when another SenseMonitor is already installed and a different exe was opened
    // ("abrir o instalado" or "instalar com este arquivo"). Pure: the decision and the words; the window only shows them.
    //
    // An OLDER file over a NEWER install must never go through on a click that looks like a routine update (the
    // installation log once showed 0.2.1 installed over 0.2.2): it is its own question, the installed copy is the
    // default, and going back needs its own explicitly worded button. The same version stays a plain reinstall.
    public enum HandOffKind
    {
        // This file is the same version or newer: the usual "open the installed one / update or reinstall with this file".
        Reinstall,
        // The installed version is newer than this file.
        Downgrade
    }

    public enum HandOffAnswer
    {
        Cancel,
        // Show or start the installed copy; nothing of this file runs elevated.
        OpenInstalled,
        // Replace the installed copy with this file (an update, a reinstall, or an explicit downgrade).
        InstallThisFile
    }

    public sealed class HandOffPrompt
    {
        public HandOffKind Kind;
        // The question, one or two sentences.
        public string Text;
        public string OpenText;
        public string InstallText;
        public string CancelText;
        // The answer the Enter key gives: always the installed copy.
        public HandOffAnswer Default;

        // installed: the installed exe's version (null = unreadable); thisExe: this file's version (null = unreadable).
        public static HandOffPrompt For(UpdateVersion installed, UpdateVersion thisExe)
        {
            HandOffPrompt p = new HandOffPrompt();
            p.Default = HandOffAnswer.OpenInstalled;
            p.OpenText = "Abrir o instalado";
            p.CancelText = "Cancelar";
            string there = installed == null ? "desconhecida" : installed.ToString();
            string mine = thisExe == null ? "desconhecida" : thisExe.ToString();
            if (InstallPlanner.NeedsDowngradeConfirmation(installed, thisExe))
            {
                p.Kind = HandOffKind.Downgrade;
                p.Text = "A versão instalada (" + there + ") é mais nova que este arquivo (" + mine + ").\n\n"
                    + "O mais seguro é abrir o que já está instalado. Instalar este arquivo troca o programa por uma versão mais antiga.";
                p.InstallText = "Instalar a versão antiga mesmo assim";
                return p;
            }
            p.Kind = HandOffKind.Reinstall;
            bool same = installed != null && thisExe != null && installed.CompareTo(thisExe) == 0;
            p.Text = "O SenseMonitor já está instalado (versão " + there + ").\n\n"
                + (same ? "Este arquivo é da mesma versão (" + mine + ")." : "Este arquivo é a versão " + mine + ".");
            p.InstallText = same ? "Reinstalar com este arquivo" : "Atualizar com este arquivo";
            return p;
        }
    }
}
