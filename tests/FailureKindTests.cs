using System;
using System.Collections.Generic;
using Forja.Core;

namespace Forja.Tests
{
    // Failure classification from the real shapes of events 41 / 6008 / 1001 (captured read-only
    // from a Windows 11 System log, machine and user names removed).
    public static class FailureKindTests
    {
        private const string Ns = "http://schemas.microsoft.com/win/2004/08/events/event";

        // Kernel-Power 41 of a blue screen 0xEF: BugcheckCode 239, button never pressed.
        public const string Xml41Bsod =
            "<Event xmlns='" + Ns + "'><System><Provider Name='Microsoft-Windows-Kernel-Power'/><EventID>41</EventID></System>"
            + "<EventData><Data Name='BugcheckCode'>239</Data><Data Name='BugcheckParameter1'>0xffff808f71844200</Data>"
            + "<Data Name='BugcheckParameter2'>0x0</Data><Data Name='SleepInProgress'>0</Data><Data Name='PowerButtonTimestamp'>0</Data>"
            + "<Data Name='LongPowerButtonPressDetected'>false</Data><Data Name='WHEABootErrorCount'>0</Data></EventData></Event>";

        // 41 without bugcheck, power button pressed, crash while the PC was going to sleep.
        public const string Xml41Hang =
            "<Event xmlns='" + Ns + "'><System><Provider Name='Microsoft-Windows-Kernel-Power'/><EventID>41</EventID></System>"
            + "<EventData><Data Name='BugcheckCode'>0</Data><Data Name='BugcheckParameter1'>0x0</Data>"
            + "<Data Name='SleepInProgress'>1</Data><Data Name='PowerButtonTimestamp'>134393236961521263</Data>"
            + "<Data Name='LongPowerButtonPressDetected'>true</Data><Data Name='WHEABootErrorCount'>2</Data></EventData></Event>";

        // 41 without bugcheck and without button: power cut or hard shutdown.
        public const string Xml41PowerLoss =
            "<Event xmlns='" + Ns + "'><System><EventID>41</EventID></System>"
            + "<EventData><Data Name='BugcheckCode'>0</Data><Data Name='SleepInProgress'>0</Data>"
            + "<Data Name='PowerButtonTimestamp'>0</Data></EventData></Event>";

        // EventLog 6008 payload: local SYSTEMTIME 2026-11-17 01:35:24.497, then the same in UTC (04:35).
        public const string Binary6008 =
            "EA070B0002001100010023001800F101EA070B0002001100040023001800F101600900003C000000010000006009000001000000B00400000100000000000000";

        public static string Xml6008()
        {
            return "<Event xmlns='" + Ns + "'><System><Provider Name='EventLog'/><EventID Qualifiers='32768'>6008</EventID></System>"
                + "<EventData><Data>01:35:24</Data><Data>17/11/2026</Data><Data></Data><Data></Data><Data>40010</Data>"
                + "<Binary>" + Binary6008 + "</Binary></EventData></Event>";
        }

        [Test]
        public static void Detail_Bsod_ReadsCodeAndNoButton()
        {
            Kernel41Event e;
            Assert.True(CrashParse.TryParseEvent41Detail(Xml41Bsod, out e), "parsed");
            Assert.Equal(239L, e.BugcheckCode);
            Assert.True(e.PayloadRead, "payload read");
            Assert.Equal(0UL, e.PowerButtonTimestamp);
            Assert.Equal(0, e.SleepInProgress);
            Assert.True(!e.LongPowerButtonPress, "no long press");
        }

        [Test]
        public static void Detail_Hang_ReadsButtonSleepAndWhea()
        {
            Kernel41Event e;
            Assert.True(CrashParse.TryParseEvent41Detail(Xml41Hang, out e), "parsed");
            Assert.Equal(0L, e.BugcheckCode);
            Assert.Equal(134393236961521263UL, e.PowerButtonTimestamp);
            Assert.Equal(1, e.SleepInProgress);
            Assert.True(e.LongPowerButtonPress, "long press");
            Assert.Equal(2, e.WheaBootErrorCount);
        }

        [Test]
        public static void Detail_Garbage_IsNotParsed()
        {
            Kernel41Event e;
            Assert.True(!CrashParse.TryParseEvent41Detail("not xml", out e), "garbage");
            Assert.Null(e);
            Assert.True(!CrashParse.TryParseEvent41Detail("<Event><EventData></EventData></Event>", out e), "no code");
        }

        [Test]
        public static void Classify_FourKinds()
        {
            Assert.Equal(FailureKind.BlueScreen, CrashParse.ClassifyFailure(true, true, 0));
            Assert.Equal(FailureKind.BlueScreen, CrashParse.ClassifyFailure(true, false, 0));
            Assert.Equal(FailureKind.HangPowerButton, CrashParse.ClassifyFailure(false, true, 5));
            Assert.Equal(FailureKind.PowerLoss, CrashParse.ClassifyFailure(false, true, 0));
            Assert.Equal(FailureKind.Unknown, CrashParse.ClassifyFailure(false, false, 0));
            Assert.Equal(FailureKind.Unknown, CrashParse.ClassifyFailure(false, false, 99));
        }

        [Test]
        public static void Down6008_FromBinary_IsTheUtcMomentInLocalTime()
        {
            DateTime? t = CrashParse.ParseDownTimeFromBinary(Binary6008);
            Assert.True(t.HasValue, "parsed");
            DateTime expected = new DateTime(2026, 11, 17, 4, 35, 24, 497, DateTimeKind.Utc).ToLocalTime();
            Assert.Equal(expected, t.Value);
        }

        [Test]
        public static void Down6008_FromXml_AndBadInput()
        {
            Assert.True(CrashParse.ParseDownTimeFromEvent6008(Xml6008()).HasValue, "xml");
            Assert.Null(CrashParse.ParseDownTimeFromEvent6008("<x/>"));
            Assert.Null(CrashParse.ParseDownTimeFromBinary("ZZ"));
            Assert.Null(CrashParse.ParseDownTimeFromBinary(""));
            // month 13 is not a date
            Assert.Null(CrashParse.ParseDownTimeFromBinary("EA070D0002001100010023001800F101EA070D0002001100040023001800F101"));
        }

        private static Kernel41Event K(string xml, DateTime when)
        {
            Kernel41Event e;
            Assert.True(CrashParse.TryParseEvent41Detail(xml, out e), "fixture parses");
            e.When = when;
            return e;
        }

        [Test]
        public static void Merge_KindsAndDownTime()
        {
            DateTime boot1 = new DateTime(2026, 11, 17, 1, 38, 27);
            // the button press in Xml41Hang (FILETIME, converted to this machine's local time) came 19 s before the restart
            DateTime press = DateTime.FromFileTimeUtc(134393236961521263L).ToLocalTime();
            DateTime boot2 = press.AddSeconds(19);
            DateTime boot3 = new DateTime(2026, 11, 10, 12, 0, 28);
            List<Kernel41Event> k = new List<Kernel41Event>();
            k.Add(K(Xml41Bsod, boot1));
            k.Add(K(Xml41Hang, boot2));
            k.Add(K(Xml41PowerLoss, boot3));
            List<Wer1001Event> w = new List<Wer1001Event>();
            Wer1001Event w1 = new Wer1001Event();
            w1.When = boot1.AddSeconds(15);
            w1.Message = "O computador foi reinicializado após uma verificação de erro. Essa verificação foi: 0x000000ef (0xffff808f71844200, 0x0000000000000000, 0xffff808f71844200, 0x0000000000000000). Um despejo de memória foi salvo em: C:\\WINDOWS\\Minidump\\111726-18578-01.dmp.";
            w.Add(w1);
            List<Unexpected6008Event> e6 = new List<Unexpected6008Event>();
            Unexpected6008Event x1 = new Unexpected6008Event();
            x1.When = boot1.AddSeconds(17);
            x1.DownAt = new DateTime(2026, 11, 17, 1, 35, 24);
            e6.Add(x1);
            Unexpected6008Event x2 = new Unexpected6008Event();
            x2.When = boot2.AddSeconds(18);
            x2.DownAt = press.AddMinutes(-21);
            e6.Add(x2);

            List<CrashEntry> r = CrashParse.Merge(k, w, e6, null);
            Assert.Equal(3, r.Count);
            // newest first
            Assert.Equal(FailureKind.BlueScreen, r[0].Kind);
            Assert.Equal(0xEFL, r[0].Code.Value);
            Assert.Equal(new DateTime(2026, 11, 17, 1, 35, 24), r[0].EffectiveTime);
            Assert.Equal(boot1, r[0].When);
            Assert.Equal(FailureKind.HangPowerButton, r[1].Kind);
            Assert.True(r[1].PowerButtonPressed, "button");
            Assert.Equal(1, r[1].SleepInProgress);
            Assert.Equal(press.AddMinutes(-21), r[1].EffectiveTime);
            Assert.Equal(FailureKind.PowerLoss, r[2].Kind);
            Assert.Equal(boot3, r[2].EffectiveTime); // no 6008: the log time stands in
        }

        private static CrashEntry OneRestart(string xml, DateTime restart, DateTime? downAt)
        {
            List<Kernel41Event> k = new List<Kernel41Event>();
            k.Add(K(xml, restart));
            List<Unexpected6008Event> e6 = new List<Unexpected6008Event>();
            if (downAt.HasValue)
            {
                Unexpected6008Event x = new Unexpected6008Event();
                x.When = restart.AddSeconds(2);
                x.DownAt = downAt;
                e6.Add(x);
            }
            return CrashParse.Merge(k, null, e6, null)[0];
        }

        [Test]
        public static void ButtonPress_BeforeTheStop_IsNotAHang_AfterTheStop_Is()
        {
            // a press that woke the notebook hours before the stop is just the last press of the session
            DateTime press = DateTime.FromFileTimeUtc(134393236961521263L).ToLocalTime();
            CrashEntry stale = OneRestart(Xml41Hang, press.AddHours(3), press.AddHours(2));
            Assert.Equal(FailureKind.PowerLoss, stale.Kind);
            Assert.True(!stale.PowerButtonPressed, "stale press");
            // pressed after the PC stopped answering: forced off
            CrashEntry forced = OneRestart(Xml41Hang, press.AddSeconds(19), press.AddMinutes(-21));
            Assert.Equal(FailureKind.HangPowerButton, forced.Kind);
            Assert.True(forced.PowerButtonPressed, "pressed after the stop");
            // no 6008: only a press within minutes of the restart counts
            Assert.Equal(FailureKind.HangPowerButton, OneRestart(Xml41Hang, press.AddSeconds(19), null).Kind);
            Assert.Equal(FailureKind.PowerLoss, OneRestart(Xml41Hang, press.AddHours(3), null).Kind);
        }

        [Test]
        public static void PowerButtonAfterStop_EdgeCases()
        {
            DateTime restart = new DateTime(2026, 11, 17, 12, 0, 0);
            Assert.True(!CrashParse.PowerButtonAfterStop(0, null, restart), "never pressed");
            Assert.True(!CrashParse.PowerButtonAfterStop(ulong.MaxValue, null, restart), "garbage timestamp");
            ulong atRestart = (ulong)restart.AddMinutes(-1).ToUniversalTime().ToFileTimeUtc();
            Assert.True(CrashParse.PowerButtonAfterStop(atRestart, null, restart), "a minute before the restart");
            Assert.True(CrashParse.PowerButtonAfterStop(atRestart, restart.AddMinutes(-20), restart), "after the stop");
            Assert.True(!CrashParse.PowerButtonAfterStop(atRestart, restart.AddMinutes(+0.5), restart), "before the stop");
        }

        [Test]
        public static void Merge_41WithoutPayload_IsUnknown_6008Alone_IsAnEntry()
        {
            DateTime boot = new DateTime(2026, 11, 12, 9, 2, 28);
            Kernel41Event bare = new Kernel41Event();
            bare.When = boot; // payload could not be parsed
            List<Kernel41Event> k = new List<Kernel41Event>();
            k.Add(bare);
            Unexpected6008Event lone = new Unexpected6008Event();
            lone.When = new DateTime(2026, 11, 13, 10, 0, 0);
            List<Unexpected6008Event> e6 = new List<Unexpected6008Event>();
            e6.Add(lone);
            List<CrashEntry> r = CrashParse.Merge(k, null, e6, null);
            Assert.Equal(2, r.Count);
            Assert.Equal(FailureKind.Unknown, r[0].Kind);
            Assert.Equal(FailureKind.Unknown, r[1].Kind);
            Assert.True(!r[0].Code.HasValue && !r[1].Code.HasValue, "no code");
        }

        [Test]
        public static void Merge_TwoRestartsClose_EachGetsItsOwn1001And6008()
        {
            DateTime b1 = new DateTime(2026, 11, 17, 1, 38, 27);
            DateTime b2 = b1.AddMinutes(1.5);                       // a second crash right after the first boot
            List<Kernel41Event> k = new List<Kernel41Event>();
            k.Add(K(Xml41Bsod, b1));
            k.Add(K(Xml41Bsod, b2));
            List<Wer1001Event> w = new List<Wer1001Event>();
            Wer1001Event w1 = new Wer1001Event();
            w1.When = b1.AddSeconds(15);
            w1.Message = "verificação: 0x000000ef (0x1, 0x0, 0x1, 0x0). Salvo em: C:\\WINDOWS\\Minidump\\a.dmp.";
            Wer1001Event w2 = new Wer1001Event();
            w2.When = b2.AddSeconds(15);
            w2.Message = "verificação: 0x0000000a (0x1, 0x0, 0x1, 0x0). Salvo em: C:\\WINDOWS\\Minidump\\b.dmp.";
            w.Add(w1);
            w.Add(w2);
            List<Unexpected6008Event> e6 = new List<Unexpected6008Event>();
            Unexpected6008Event x1 = new Unexpected6008Event();
            x1.When = b1.AddSeconds(17);
            x1.DownAt = b1.AddMinutes(-3);
            Unexpected6008Event x2 = new Unexpected6008Event();
            x2.When = b2.AddSeconds(17);
            x2.DownAt = b2.AddMinutes(-1.2);
            e6.Add(x1);
            e6.Add(x2);
            List<CrashEntry> r = CrashParse.Merge(k, w, e6, null);
            Assert.Equal(2, r.Count);
            Assert.Equal(b2.AddMinutes(-1.2), r[0].DownAt.Value);
            Assert.Equal(0xEFL, r[0].Code.Value);                   // the 41 code wins over the 1001 text (0xA) of the same restart
            Assert.True(r[0].DumpPath == null || r[0].DumpPath.EndsWith("b.dmp"), "second restart keeps its own dump");
            Assert.Equal(b1.AddMinutes(-3), r[1].DownAt.Value);
        }

        [Test]
        public static void Merge_1001ArrivingLaterThanTheWindow_IsItsOwnEntry()
        {
            DateTime boot = new DateTime(2026, 11, 17, 1, 38, 27);
            List<Kernel41Event> k = new List<Kernel41Event>();
            k.Add(K(Xml41PowerLoss, boot));
            List<Wer1001Event> w = new List<Wer1001Event>();
            Wer1001Event late = new Wer1001Event();
            late.When = boot.AddMinutes(5);
            late.Message = "verificação: 0x0000000a (0x1, 0x0, 0x1, 0x0).";
            w.Add(late);
            List<CrashEntry> r = CrashParse.Merge(k, w, null);
            Assert.Equal(2, r.Count);
            Assert.Equal(FailureKind.BlueScreen, r[0].Kind);           // the late 1001 (newest first)
            Assert.Equal(FailureKind.PowerLoss, r[1].Kind);
        }

        [Test]
        public static void Merge_OldSignature_StillWorks()
        {
            List<Kernel41Event> k = new List<Kernel41Event>();
            Kernel41Event e = new Kernel41Event();
            e.When = new DateTime(2026, 11, 12, 9, 0, 0);
            e.BugcheckCode = 0x1C7;
            k.Add(e);
            List<CrashEntry> r = CrashParse.Merge(k, null, null);
            Assert.Equal(1, r.Count);
            Assert.Equal(FailureKind.BlueScreen, r[0].Kind);
        }

        [Test]
        public static void DyingProcess_OnlyWhenTheTextNamesAnExecutable()
        {
            Assert.Null(CrashParse.ParseDyingProcessFromMessage(
                "Essa verificação foi: 0x000000ef (0xffff808f71844200, 0x0, 0xffff808f71844200, 0x0). Um despejo de memória foi salvo em: C:\\WINDOWS\\Minidump\\111726-18578-01.dmp."));
            Assert.Equal("csrss.exe", CrashParse.ParseDyingProcessFromMessage("Critical process died: csrss.exe (pid 812)"));
            Assert.Null(CrashParse.ParseDyingProcessFromMessage("saved in C:\\Tools\\thing.exe"));
            Assert.Null(CrashParse.ParseDyingProcessFromMessage(null));
        }

        [Test]
        public static void Ago_Short()
        {
            Assert.Equal("há menos de 1 min", FailureText.AgoShort(TimeSpan.FromSeconds(30)));
            Assert.Equal("há 35 min", FailureText.AgoShort(TimeSpan.FromMinutes(35)));
            Assert.Equal("há 9 h", FailureText.AgoShort(TimeSpan.FromHours(9.4)));
            Assert.Equal("há 2 d", FailureText.AgoShort(TimeSpan.FromHours(50)));
            Assert.Equal("há 1 d", FailureText.AgoShort(TimeSpan.FromHours(24)));
            Assert.Equal("agora", FailureText.AgoShort(TimeSpan.FromMinutes(-3)));
        }

        [Test]
        public static void KindWords()
        {
            Assert.Equal("falha do sistema", FailureText.KindShort(FailureKind.BlueScreen));
            Assert.Equal("travamento", FailureText.KindShort(FailureKind.HangPowerButton));
            Assert.Equal("queda de energia", FailureText.KindShort(FailureKind.PowerLoss));
            Assert.Equal("reinício inesperado", FailureText.KindShort(FailureKind.Unknown));
            CrashEntry e = new CrashEntry();
            e.Code = 0xEF;
            e.Kind = FailureKind.BlueScreen;
            Assert.Equal("falha do sistema (0xEF)", FailureText.Brief(e));
        }

        [Test]
        public static void KindLong_HasNoBlueScreenWording()
        {
            foreach (FailureKind k in new FailureKind[] { FailureKind.Unknown, FailureKind.BlueScreen, FailureKind.HangPowerButton, FailureKind.PowerLoss })
            {
                Assert.True(!FailureText.KindLong(k).ToLowerInvariant().Contains("tela azul"), k.ToString());
                Assert.True(!FailureText.KindShort(k).ToLowerInvariant().Contains("tela azul"), k.ToString());
            }
        }
    }
}
