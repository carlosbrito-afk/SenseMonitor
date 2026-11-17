using Forja.Core;

namespace Forja.Tests
{
    // The hand-off question when another SenseMonitor is installed: an older file over a newer install is its own question.
    public static class HandOffPromptTests
    {
        private static UpdateVersion V(int a, int b, int c) { return new UpdateVersion(a, b, c); }

        [Test]
        public static void OlderFileOverNewerInstall_IsADowngradeQuestion_InstalledIsTheDefault()
        {
            HandOffPrompt p = HandOffPrompt.For(V(0, 2, 2), V(0, 2, 1));
            Assert.Equal(HandOffKind.Downgrade, p.Kind);
            Assert.Equal(HandOffAnswer.OpenInstalled, p.Default);
            Assert.Equal("Abrir o instalado", p.OpenText);
            Assert.Equal("Instalar a versão antiga mesmo assim", p.InstallText);
            Assert.True(p.Text.Contains("A versão instalada (0.2.2) é mais nova que este arquivo (0.2.1)"), p.Text);
        }

        [Test]
        public static void SameVersion_IsAPlainReinstall()
        {
            HandOffPrompt p = HandOffPrompt.For(V(0, 2, 2), V(0, 2, 2));
            Assert.Equal(HandOffKind.Reinstall, p.Kind);
            Assert.Equal("Reinstalar com este arquivo", p.InstallText);
            Assert.True(!p.Text.Contains("mais nova"), p.Text);
            Assert.Equal(HandOffAnswer.OpenInstalled, p.Default);
        }

        [Test]
        public static void NewerFile_IsAnUpdate()
        {
            HandOffPrompt p = HandOffPrompt.For(V(0, 2, 2), V(0, 2, 3));
            Assert.Equal(HandOffKind.Reinstall, p.Kind);
            Assert.Equal("Atualizar com este arquivo", p.InstallText);
            Assert.True(p.Text.Contains("0.2.2") && p.Text.Contains("0.2.3"), p.Text);
        }

        [Test]
        public static void Comparison_IsNumeric_NotTextual()
        {
            Assert.Equal(HandOffKind.Downgrade, HandOffPrompt.For(V(0, 10, 0), V(0, 9, 0)).Kind);
            Assert.Equal(HandOffKind.Reinstall, HandOffPrompt.For(V(0, 9, 0), V(0, 10, 0)).Kind);
            Assert.Equal(HandOffKind.Downgrade, HandOffPrompt.For(V(1, 0, 0), V(0, 99, 99)).Kind);
        }

        [Test]
        public static void UnreadableVersion_NeverClaimsADowngrade_AndAlwaysDefaultsToTheInstalled()
        {
            HandOffPrompt a = HandOffPrompt.For(null, V(0, 2, 1));
            Assert.Equal(HandOffKind.Reinstall, a.Kind);
            Assert.True(a.Text.Contains("desconhecida"), a.Text);
            HandOffPrompt b = HandOffPrompt.For(V(0, 2, 2), null);
            Assert.Equal(HandOffKind.Reinstall, b.Kind);
            Assert.Equal(HandOffAnswer.OpenInstalled, b.Default);
        }
    }
}
