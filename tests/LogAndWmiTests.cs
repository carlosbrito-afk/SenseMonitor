using System;
using System.IO;
using Forja.Core;

namespace Forja.Tests
{
    // erro.log without personal data, and the pacing of WMI reconnection.
    public static class LogAndWmiTests
    {
        // ---- erro.log ----

        [Test]
        public static void LogText_Scrub_ReplacesProfilePathAndUserName()
        {
            string s = LogText.Scrub(@"Falhou em C:\Users\Fulano\AppData\Roaming\x.json e c:\users\fulano\Desktop; D:\Dados\Fulano\a.txt",
                @"C:\Users\Fulano", "Fulano");
            Assert.True(!s.ToLowerInvariant().Contains("fulano"), s);
            Assert.True(s.Contains(@"%USERPROFILE%\AppData\Roaming\x.json"), s);
            Assert.True(s.Contains(@"%USERPROFILE%\Desktop"), s);
        }

        [Test]
        public static void LogText_Describe_TypeAndMessageWithoutPersonalData()
        {
            Exception ex;
            try { throw new IOException(@"Acesso negado a C:\Users\Fulano\secret.txt", new UnauthorizedAccessException("inner")); }
            catch (Exception e) { ex = e; }
            string s = LogText.Describe(ex, @"C:\Users\Fulano", "Fulano");
            Assert.True(s.StartsWith("System.IO.IOException: Acesso negado a %USERPROFILE%\\secret.txt"), s);
            Assert.True(s.Contains("System.UnauthorizedAccessException: inner"), "inner exception named: " + s);
            Assert.True(!s.Contains("Fulano"), s);
            Assert.Equal("(sem detalhes)", LogText.Describe(null, null, null));
        }

        // ---- WMI reconnect pacing ----

        [Test]
        public static void Reconnect_AfterFiveFailures_AtMostEvery30s()
        {
            ReconnectPolicy p = new ReconnectPolicy();
            for (int i = 0; i < 4; i++) Assert.True(!p.Failure(1000 + i), "not yet");
            Assert.True(p.Failure(1010), "fifth consecutive failure reconnects");
            for (int i = 0; i < 20; i++) Assert.True(!p.Failure(2000 + i), "no hammering");
            Assert.True(p.Failure(31010), "after 30 s and five more failures");
        }

        [Test]
        public static void Reconnect_SuccessResetsTheCount()
        {
            ReconnectPolicy p = new ReconnectPolicy();
            for (int i = 0; i < 4; i++) p.Failure(i);
            p.Success();
            for (int i = 0; i < 4; i++) Assert.True(!p.Failure(10 + i), "count restarted");
            Assert.True(p.Failure(20), "fifth after the reset");
        }
    }
}
