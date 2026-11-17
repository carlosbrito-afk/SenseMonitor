namespace Forja.Core
{
    // The release public key, embedded in the app. Updates are accepted only when the manifest
    // signature verifies against THIS key.
    //
    // PLACEHOLDER: until the real key is pasted below the updater refuses to update at all.
    // Create the key pair once with   bin\ForjaRelease.exe gerar-chave   (it prints the line to
    // paste here) and keep the private key file backed up: without it no further update can be
    // signed, and installed copies could only be replaced by hand. Never commit a private key.
    public static class UpdateKey
    {
        public const string PlaceholderMarker = "<<CHAVE-PUBLICA-AINDA-NAO-CONFIGURADA>>";

        // >>> paste the output of "gerar-chave" over the next line (keep it a single constant) <<<
        public const string PublicKeyXml = @"<RSAKeyValue><Modulus>pxo9FTvXdYZrLIi9h3KYuSBENuo3VHKvAOyktDkMAGmii0AXJK1I31dL/Wa523r9TrOVkTMXjzVoe3ndwUHT0jP55AH13ZNPPGWkZbpV6y6o+YneRjSJkDhF4dyEGFFuVv9WYdTvaSKLjFFWpbGb8hhtzTRO74sz6Apq6sGDYututy+pihIBB3yFYdKyRxDP6XWUf30g7bFmFEu/9utpQ6CoFj7l0iNbhFnkx8TJEoHagw5UEK23fEaudPiE8fXfQJdbJi/Ie1QP7gNH0RImI7bn44jlvnU0ryAQdxQJ5iNrlO3ZXbYVl4cf1bUQqIa/W5yuJbRBAtM67bklv6EIK3b2p9q6zIP4Ww5xg5hv6TGHUQKoGCtURqeGunfuwq77WYSvlvU1Ad4FQhm4qtVxD8iPb59/2vuguxUOB5t0sIjLI7fpCS6ukXu5TIG3ho2v8JiTPSYFu1SjkQNhWfhLckCht9Dnn/FNZpdd8ewUz4ajBv9sYegU946cA2wroRnh</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";

        // True only for something shaped like a public RSA key; the placeholder (or an empty
        // or garbled value) is "not configured" and disables updating.
        public static bool IsConfigured(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return false;
            if (xml.IndexOf("<<", System.StringComparison.Ordinal) >= 0) return false;
            return xml.IndexOf("<RSAKeyValue>", System.StringComparison.Ordinal) >= 0
                && xml.IndexOf("<Modulus>", System.StringComparison.Ordinal) >= 0;
        }

        public static bool Configured
        {
            get { return IsConfigured(PublicKeyXml); }
        }
    }
}
