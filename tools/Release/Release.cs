using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Forja.Core;

// ForjaRelease: the release-side tool of the signed updater. NOT part of the app and never shipped.
//
//   ForjaRelease gerar-chave                                 one-time: creates the (encrypted) private key
//   ForjaRelease assinar <arquivo>                           writes <arquivo>.sig (base64)
//   ForjaRelease manifesto <exe> <versao> <notas.txt> <out>  writes manifest.json for that exe
//   ForjaRelease verificar <arquivo> <arquivo.sig>           checks a signature with the key embedded in the app
//
// The private key lives only in %USERPROFILE%\.sensemonitor-release\chave-privada.enc, encrypted with a
// passphrase (PBKDF2-SHA256 600,000 iterations, AES-256-CBC, HMAC-SHA256; see KeyVault) and readable by the
// current user alone. The passphrase is typed at gerar-chave and at every signing, read from the console
// without echo; it is never stored and there is deliberately no way to pass it on the command line or in the
// environment. Losing the passphrase or the file means the key is gone: installed copies could then only be
// replaced by a manual reinstall.
namespace Forja.ReleaseTool
{
    public static class Program
    {
        private const string KeyDirName = ".sensemonitor-release";
        private const string KeyFileName = "chave-privada.enc";
        private const string LegacyPlainName = "chave-privada.xml";

        public static int Main(string[] args)
        {
            try
            {
                if (args.Length == 0) return Usage();
                string cmd = args[0].ToLowerInvariant();
                if (cmd == "gerar-chave" && args.Length == 1) return GenerateKey();
                if (cmd == "assinar" && args.Length == 2) return Sign(args[1]);
                if (cmd == "manifesto" && args.Length == 5) return Manifest(args[1], args[2], args[3], args[4]);
                if (cmd == "verificar" && args.Length == 3) return Verify(args[1], args[2]);
                return Usage();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ERRO: " + ex.Message);
                return 1;
            }
        }

        private static int Usage()
        {
            Console.Error.WriteLine("Uso:");
            Console.Error.WriteLine("  ForjaRelease gerar-chave");
            Console.Error.WriteLine("  ForjaRelease assinar <arquivo>");
            Console.Error.WriteLine("  ForjaRelease manifesto <SenseMonitor.exe> <versao> <arquivo-de-notas.txt> <manifest.json>");
            Console.Error.WriteLine("  ForjaRelease verificar <arquivo> <arquivo.sig>");
            return 2;
        }

        private static string KeyDir
        {
            get
            {
#if TESTE_PASSFRASE
                // Compiled only into scratch builds used to exercise the tool; the shipped tool has neither hook.
                string over = Environment.GetEnvironmentVariable("SENSEMONITOR_KEYDIR_TESTE");
                if (!string.IsNullOrEmpty(over)) return over;
#endif
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), KeyDirName);
            }
        }

        private static string KeyPath
        {
            get { return Path.Combine(KeyDir, KeyFileName); }
        }

        // ---- passphrase (console, no echo) -----------------------------------------------------

        private static string ReadSecret(string prompt)
        {
#if TESTE_PASSFRASE
            string fromTest = Environment.GetEnvironmentVariable("FORJA_TESTE_PASS");
            if (fromTest != null) return fromTest;
#endif
            if (Console.IsInputRedirected)
            {
                throw new InvalidOperationException("a frase-senha só é lida de um console interativo (nunca por argumento, variável de ambiente ou arquivo)");
            }
            Console.Error.Write(prompt);
            StringBuilder sb = new StringBuilder();
            while (true)
            {
                ConsoleKeyInfo k = Console.ReadKey(true);
                if (k.Key == ConsoleKey.Enter) break;
                if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
                if (k.KeyChar != '\0' && !char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
            }
            Console.Error.WriteLine();
            return sb.ToString();
        }

        // ---- the key file's own protection ------------------------------------------------------

        // The file must belong to the current user and grant access to nobody else.
        private static string KeyFileProblem(string path)
        {
            SecurityIdentifier me = WindowsIdentity.GetCurrent().User;
            FileSecurity fs = new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            IdentityReference owner = fs.GetOwner(typeof(SecurityIdentifier));
            if (owner == null || owner.Value != me.Value) return "o dono do arquivo da chave não é o usuário atual";
            foreach (FileSystemAccessRule r in fs.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (r.AccessControlType == AccessControlType.Allow && r.IdentityReference.Value != me.Value)
                {
                    return "o arquivo da chave dá acesso a outra conta (" + r.IdentityReference.Value + ")";
                }
            }
            return null;
        }

        // ---- gerar-chave ---------------------------------------------------------------------

        private static int GenerateKey()
        {
            string dir = KeyDir;
            string path = KeyPath;
            string legacy = Path.Combine(dir, LegacyPlainName);
            if (File.Exists(path) || Directory.Exists(path) || File.Exists(legacy))
            {
                Console.Error.WriteLine("RECUSADO: já existe uma chave privada em " + dir);
                Console.Error.WriteLine("Não é sobrescrita (isso invalidaria todas as atualizações já instaladas). Apague o arquivo à mão só se tiver certeza.");
                return 1;
            }
            string pass = ReadSecret("Frase-senha da chave de assinatura (mínimo " + KeyVault.MinPassphraseChars + " caracteres): ");
            string problem = KeyVault.PassphraseProblem(pass);
            if (problem != null) { Console.Error.WriteLine("RECUSADO: " + problem + "."); return 1; }
            string again = ReadSecret("Repita a frase-senha: ");
            if (!string.Equals(pass, again, StringComparison.Ordinal)) { Console.Error.WriteLine("RECUSADO: as duas frases-senha não são iguais."); return 1; }

            SecurityIdentifier me = WindowsIdentity.GetCurrent().User;
            if (me == null) throw new InvalidOperationException("não foi possível identificar o usuário atual");

            // Only this user: no inheritance, no administrators, no SYSTEM entry.
            DirectorySecurity ds = new DirectorySecurity();
            ds.SetAccessRuleProtection(true, false);
            ds.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir, ds);
            else new DirectoryInfo(dir).SetAccessControl(ds);

            string privateXml = UpdateSigner.NewPrivateKeyXml(UpdateVerifier.MinKeyBits);
            byte[] encrypted = KeyVault.Protect(privateXml, pass, KeyVault.DefaultIterations);
            FileSecurity fsec = new FileSecurity();
            fsec.SetAccessRuleProtection(true, false);
            fsec.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl, AccessControlType.Allow));
            using (FileStream f = new FileStream(path, FileMode.CreateNew, FileSystemRights.WriteData | FileSystemRights.ReadPermissions,
                FileShare.None, 4096, FileOptions.None, fsec))
            {
                f.Write(encrypted, 0, encrypted.Length);
            }

            string publicXml = UpdateSigner.PublicPart(privateXml);
            Console.WriteLine("Chave privada criada, criptografada com a sua frase-senha (e só o seu usuário lê): " + path);
            Console.WriteLine("FAÇA BACKUP desse arquivo num lugar seguro e privado E guarde a frase-senha num gerenciador de senhas.");
            Console.WriteLine("Perder o arquivo OU a frase-senha = perder a chave: ninguém mais recebe atualizações assinadas e quem");
            Console.WriteLine("já instalou teria de reinstalar à mão. A frase-senha não fica gravada em lugar nenhum. Nunca envie ao GitHub.");
            Console.WriteLine();
            Console.WriteLine("Cole a linha abaixo em src\\Core\\UpdateKey.cs, no lugar de 'public const string PublicKeyXml = ...;':");
            Console.WriteLine();
            Console.WriteLine("        public const string PublicKeyXml = @\"" + publicXml.Replace("\"", "\"\"") + "\";");
            Console.WriteLine();
            Console.WriteLine("Depois rode build.cmd all e publique a primeira versão com publish\\lancar.ps1.");
            return 0;
        }

        // ---- assinar -------------------------------------------------------------------------

        private static string ReadPrivateKey()
        {
            string path = KeyPath;
            if (!File.Exists(path)) throw new InvalidOperationException("chave privada não encontrada em " + path + " (rode gerar-chave)");
            string acl = KeyFileProblem(path);
            if (acl != null) throw new InvalidOperationException("RECUSADO: " + acl + ". Corrija as permissões (só o seu usuário) antes de assinar.");
            byte[] file = File.ReadAllBytes(path);
            string pass = ReadSecret("Frase-senha da chave de assinatura: ");
            string err;
            string xml = KeyVault.Unprotect(file, pass, out err);
            if (xml == null) throw new InvalidOperationException(err);
            return xml;
        }

        private static int Sign(string file)
        {
            byte[] data = File.ReadAllBytes(file);
            if (!UpdateKey.Configured)
            {
                Console.Error.WriteLine("RECUSADO: src\\Core\\UpdateKey.cs ainda tem a chave de exemplo. O app não aceitaria esta assinatura.");
                return 1;
            }
            string priv = ReadPrivateKey();
            string sig = UpdateSigner.Sign(data, priv);
            // Never publish something the app would reject: check against the key embedded in the app.
            SignatureCheck chk = UpdateVerifier.Verify(data, sig, UpdateKey.PublicKeyXml);
            if (chk != SignatureCheck.Ok)
            {
                Console.Error.WriteLine("RECUSADO: a assinatura não confere com a chave pública embutida no app (" + UpdateVerifier.Describe(chk) + ").");
                Console.Error.WriteLine("A chave privada em uso não é a que gerou UpdateKey.cs, ou o app foi compilado antes de colar a chave.");
                return 1;
            }
            File.WriteAllText(file + ".sig", sig, new UTF8Encoding(false));
            Console.WriteLine("assinado: " + file + ".sig");
            return 0;
        }

        // ---- manifesto -----------------------------------------------------------------------

        private static int Manifest(string exe, string versao, string notesFile, string output)
        {
            UpdateVersion v;
            if (!UpdateVersion.TryParse(versao, out v)) throw new ArgumentException("versão inválida (use x.y.z): " + versao);
            string embedded = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).ProductVersion;
            if (embedded != versao)
            {
                Console.Error.WriteLine("RECUSADO: o executável diz que é a versão '" + embedded + "', não '" + versao + "'.");
                return 1;
            }
            string notes = File.Exists(notesFile) ? File.ReadAllText(notesFile, Encoding.UTF8).Trim() : "";
            byte[] exeBytes = File.ReadAllBytes(exe);
            byte[] json = UpdateManifestFile.Build(versao, UpdateManifestFile.Sha256Hex(exeBytes), exeBytes.Length,
                DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture), notes, null);
            File.WriteAllBytes(output, json);
            Console.WriteLine("manifesto: " + output + " (" + exeBytes.Length + " bytes, sha256 " + UpdateManifestFile.Sha256Hex(exeBytes) + ")");
            return 0;
        }

        // ---- verificar -----------------------------------------------------------------------

        private static int Verify(string file, string sigFile)
        {
            SignatureCheck r = UpdateVerifier.Verify(File.ReadAllBytes(file), File.ReadAllText(sigFile), UpdateKey.PublicKeyXml);
            Console.WriteLine(UpdateVerifier.Describe(r));
            return r == SignatureCheck.Ok ? 0 : 1;
        }
    }
}
