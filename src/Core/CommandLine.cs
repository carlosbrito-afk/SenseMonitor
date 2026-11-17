using System;
using System.Collections.Generic;
using System.Text;

namespace Forja.Core
{
    // Windows command-line splitting and quoting with the CommandLineToArgvW / MSVC rules, so that what we
    // classify (Win32_Process.CommandLine) and what we pass on (the elevated relaunch) is what the process
    // itself will see.
    public static class CommandLine
    {
        // First token = the program: quoted up to the next quote (no escapes) or up to the first blank.
        // The others: blanks separate outside quotes; 2n backslashes + a quote = n backslashes and the quote
        // toggles; 2n+1 backslashes + a quote = n backslashes and a literal quote; backslashes not followed
        // by a quote are literal.
        public static List<string> Split(string commandLine)
        {
            List<string> tokens = new List<string>();
            if (string.IsNullOrEmpty(commandLine)) return tokens;
            int i = 0, n = commandLine.Length;
            while (i < n && IsBlank(commandLine[i])) i++;
            if (i >= n) return tokens;

            StringBuilder prog = new StringBuilder();
            if (commandLine[i] == '"')
            {
                i++;
                while (i < n && commandLine[i] != '"') prog.Append(commandLine[i++]);
                if (i < n) i++; // closing quote
            }
            else
            {
                while (i < n && !IsBlank(commandLine[i])) prog.Append(commandLine[i++]);
            }
            tokens.Add(prog.ToString());

            while (true)
            {
                while (i < n && IsBlank(commandLine[i])) i++;
                if (i >= n) break;
                StringBuilder cur = new StringBuilder();
                bool inQuotes = false;
                while (i < n)
                {
                    char c = commandLine[i];
                    if (c == '\\')
                    {
                        int slashes = 0;
                        while (i < n && commandLine[i] == '\\') { slashes++; i++; }
                        if (i < n && commandLine[i] == '"')
                        {
                            cur.Append('\\', slashes / 2);
                            if (slashes % 2 == 1) { cur.Append('"'); i++; }
                            else { inQuotes = !inQuotes; i++; }
                        }
                        else cur.Append('\\', slashes);
                        continue;
                    }
                    if (c == '"') { inQuotes = !inQuotes; i++; continue; }
                    if (!inQuotes && IsBlank(c)) break;
                    cur.Append(c);
                    i++;
                }
                tokens.Add(cur.ToString());
            }
            return tokens;
        }

        // The inverse of Split for one argument: no quotes unless needed; backslashes are doubled only
        // where they precede a quote (and before the closing quote).
        public static string Quote(string arg)
        {
            if (arg == null) arg = "";
            if (arg.Length > 0 && arg.IndexOfAny(new char[] { ' ', '\t', '"' }) < 0) return arg;
            StringBuilder sb = new StringBuilder();
            sb.Append('"');
            int i = 0;
            while (i < arg.Length)
            {
                int slashes = 0;
                while (i < arg.Length && arg[i] == '\\') { slashes++; i++; }
                if (i == arg.Length)
                {
                    sb.Append('\\', slashes * 2);
                    break;
                }
                if (arg[i] == '"')
                {
                    sb.Append('\\', slashes * 2 + 1);
                    sb.Append('"');
                }
                else
                {
                    sb.Append('\\', slashes);
                    sb.Append(arg[i]);
                }
                i++;
            }
            sb.Append('"');
            return sb.ToString();
        }

        private static bool IsBlank(char c)
        {
            return c == ' ' || c == '\t';
        }
    }
}
