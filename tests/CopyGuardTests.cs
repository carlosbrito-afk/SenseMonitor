using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Forja.Tests
{
    // The product is for every owner of the notebook: no user-visible text may say "tela azul".
    // Scans the string literals (comments are skipped) of the shipped sources.
    public static class CopyGuardTests
    {
        private static string FindSourceRoot()
        {
            string forced = Environment.GetEnvironmentVariable("FORJA_SRC");
            if (!string.IsNullOrEmpty(forced) && Directory.Exists(forced)) return forced;
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
            {
                string src = Path.Combine(dir, "src");
                if (Directory.Exists(Path.Combine(src, "Core")) && Directory.Exists(Path.Combine(src, "App"))) return src;
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        // String literals of a C# source, comments left out. Handles "..." with escapes, @"..." and char literals.
        public static List<string> Literals(string code)
        {
            List<string> list = new List<string>();
            int i = 0;
            int n = code.Length;
            while (i < n)
            {
                char c = code[i];
                if (c == '/' && i + 1 < n && code[i + 1] == '/')
                {
                    while (i < n && code[i] != '\n') i++;
                }
                else if (c == '/' && i + 1 < n && code[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < n && !(code[i] == '*' && code[i + 1] == '/')) i++;
                    i += 2;
                }
                else if (c == '\'')
                {
                    i++;
                    while (i < n && code[i] != '\'')
                    {
                        if (code[i] == '\\') i++;
                        i++;
                    }
                    i++;
                }
                else if (c == '@' && i + 1 < n && code[i + 1] == '"')
                {
                    i += 2;
                    StringBuilder sb = new StringBuilder();
                    while (i < n)
                    {
                        if (code[i] == '"')
                        {
                            if (i + 1 < n && code[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                            break;
                        }
                        sb.Append(code[i++]);
                    }
                    i++;
                    list.Add(sb.ToString());
                }
                else if (c == '"')
                {
                    i++;
                    StringBuilder sb = new StringBuilder();
                    while (i < n && code[i] != '"')
                    {
                        if (code[i] == '\\' && i + 1 < n) { sb.Append(code[i]); i++; }
                        sb.Append(code[i++]);
                    }
                    i++;
                    list.Add(sb.ToString());
                }
                else i++;
            }
            return list;
        }

        [Test]
        public static void Literals_Scanner_SkipsCommentsAndFindsStrings()
        {
            List<string> l = Literals("// \"no\"\nvar a = \"tela azul\"; /* \"nem isto\" */ var b = @\"x \"\"y\"\"\"; char c = '\"'; var d = \"q\\\"r\";");
            Assert.Equal(3, l.Count);
            Assert.Equal("tela azul", l[0]);
            Assert.Equal("x \"y\"", l[1]);
            Assert.Equal("q\\\"r", l[2]);
        }

        [Test]
        public static void NoUserVisibleText_SaysBlueScreen()
        {
            string src = FindSourceRoot();
            // A scan that finds nothing to scan must fail, not pass: set FORJA_SRC to the src folder if the tests run elsewhere.
            Assert.True(src != null, "the src folder was not found (set FORJA_SRC); the copy guard did not scan anything");
            List<string> hits = new List<string>();
            int files = 0;
            foreach (string folder in new string[] { "App", "Core", "Platform" })
            {
                string dir = Path.Combine(src, folder);
                if (!Directory.Exists(dir)) continue;
                foreach (string file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
                {
                    files++;
                    string code = File.ReadAllText(file, Encoding.UTF8);
                    foreach (string lit in Literals(code))
                    {
                        string low = lit.ToLowerInvariant();
                        if (low.Contains("tela azul") || low.Contains("telas azuis") || low.Contains("tela-azul")
                            || low.Contains("blue screen") || low.Contains("bsod"))
                        {
                            hits.Add(Path.GetFileName(file) + ": " + lit);
                        }
                    }
                }
            }
            Assert.True(files > 40, "the guard scanned only " + files + " files");
            Assert.True(hits.Count == 0, "user-visible blue-screen text: " + string.Join(" | ", hits.ToArray()));
        }
    }
}
