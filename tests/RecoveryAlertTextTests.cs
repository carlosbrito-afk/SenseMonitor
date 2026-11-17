using System.Collections.Generic;
using Forja.Core;

namespace Forja.Tests
{
    // The journal lines of start-up recovery feed the alert classifier: a start that writes nothing (untested model,
    // postponed retry, foreign marker) must never read as "Automático não confirmado" (that would be a red alert at
    // every start).
    public static class RecoveryAlertTextTests
    {
        private sealed class ListJournal : IJournal
        {
            public readonly List<string> Lines = new List<string>();
            public void Write(string kind, string text) { Lines.Add(kind + " " + text); }
        }

        private sealed class Flag : IFlag
        {
            public bool On;
            public bool IsSet { get { return On; } }
            public bool Set() { On = true; return true; }
            public bool Clear() { On = false; return true; }
        }

        [Test]
        public static void NoWriteOnAnUntestedModel_SaysNothingAlarming()
        {
            ListJournal j = new ListJournal();
            Flag session = new Flag();
            session.On = true;
            RecoveryContext c = new RecoveryContext();
            c.FanControlConfirmed = false;
            StartupRecovery.Run(new GuardedFirmware(new FakeFirmware(), NullJournal.Instance, null, false, null, null), new Flag(), session, j, c);
            foreach (string l in j.Lines)
            {
                Assert.True(!l.Contains("não confirmado") && !l.Contains("NÃO confirmado"), "alarming wording: " + l);
            }
            Assert.True(j.Lines.Exists(delegate(string l) { return l.Contains("nada escrito"); }), "it says nothing was written");
        }
    }
}
