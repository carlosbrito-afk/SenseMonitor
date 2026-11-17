using System;
using System.Security.Cryptography;
using System.Text;
using Forja.Core;

namespace Forja.Tests
{
    // Throwaway keys, generated in memory once per test run (3072-bit generation is slow).
    public static class TestKeys
    {
        private static string privA, privB, priv2048;

        public static string PrivA { get { return privA ?? (privA = UpdateSigner.NewPrivateKeyXml(3072)); } }
        public static string PubA { get { return UpdateSigner.PublicPart(PrivA); } }
        public static string PrivB { get { return privB ?? (privB = UpdateSigner.NewPrivateKeyXml(3072)); } }
        public static string PubB { get { return UpdateSigner.PublicPart(PrivB); } }
        public static string Priv2048 { get { return priv2048 ?? (priv2048 = UpdateSigner.NewPrivateKeyXml(2048)); } }
    }

    public static class UpdateSigningTests
    {
        private static byte[] Data(string s) { return Encoding.UTF8.GetBytes(s); }

        [Test]
        public static void SignVerify_RoundTrip()
        {
            byte[] d = Data("{\"versao\":\"0.2.1\"}");
            string sig = UpdateSigner.Sign(d, TestKeys.PrivA);
            Assert.Equal(SignatureCheck.Ok, UpdateVerifier.Verify(d, sig, TestKeys.PubA));
        }

        [Test]
        public static void Verify_TrailingNewlineInSigFile_IsFine()
        {
            byte[] d = Data("manifest");
            string sig = UpdateSigner.Sign(d, TestKeys.PrivA) + "\r\n";
            Assert.Equal(SignatureCheck.Ok, UpdateVerifier.Verify(d, sig, TestKeys.PubA));
        }

        [Test]
        public static void Verify_TamperedData_Fails()
        {
            byte[] d = Data("manifest v1");
            string sig = UpdateSigner.Sign(d, TestKeys.PrivA);
            Assert.Equal(SignatureCheck.BadSignature, UpdateVerifier.Verify(Data("manifest v2"), sig, TestKeys.PubA));
            byte[] e = (byte[])d.Clone();
            e[0] ^= 1; // a single flipped bit
            Assert.Equal(SignatureCheck.BadSignature, UpdateVerifier.Verify(e, sig, TestKeys.PubA));
        }

        [Test]
        public static void Verify_TamperedSignature_Fails()
        {
            byte[] d = Data("manifest");
            byte[] sig = Convert.FromBase64String(UpdateSigner.Sign(d, TestKeys.PrivA));
            sig[10] ^= 0x40;
            Assert.Equal(SignatureCheck.BadSignature, UpdateVerifier.Verify(d, Convert.ToBase64String(sig), TestKeys.PubA));
        }

        [Test]
        public static void Verify_OtherKey_Fails()
        {
            byte[] d = Data("manifest");
            string sig = UpdateSigner.Sign(d, TestKeys.PrivB);
            Assert.Equal(SignatureCheck.BadSignature, UpdateVerifier.Verify(d, sig, TestKeys.PubA));
        }

        [Test]
        public static void Verify_MalformedSignatures_Fail()
        {
            byte[] d = Data("manifest");
            string good = UpdateSigner.Sign(d, TestKeys.PrivA);
            string[] bad = { null, "", "   ", "not base64 !!", "AAAA", good.Substring(0, good.Length - 8), good + good, new string('A', 5000) };
            foreach (string s in bad)
            {
                Assert.Equal(SignatureCheck.BadSignature, UpdateVerifier.Verify(d, s, TestKeys.PubA));
            }
        }

        [Test]
        public static void Verify_EmptyOrNullData_Fails()
        {
            string sig = UpdateSigner.Sign(Data("x"), TestKeys.PrivA);
            Assert.Equal(SignatureCheck.BadSignature, UpdateVerifier.Verify(null, sig, TestKeys.PubA));
            Assert.Equal(SignatureCheck.BadSignature, UpdateVerifier.Verify(new byte[0], sig, TestKeys.PubA));
        }

        [Test]
        public static void Verify_Placeholder_IsBadKey()
        {
            string sig = UpdateSigner.Sign(Data("x"), TestKeys.PrivA);
            Assert.Equal(SignatureCheck.BadKey, UpdateVerifier.Verify(Data("x"), sig, UpdateKey.PlaceholderMarker));
            Assert.Equal(SignatureCheck.BadKey, UpdateVerifier.Verify(Data("x"), sig, null));
            Assert.Equal(SignatureCheck.BadKey, UpdateVerifier.Verify(Data("x"), sig, ""));
            Assert.Equal(SignatureCheck.BadKey, UpdateVerifier.Verify(Data("x"), sig, "<RSAKeyValue><Modulus>AQAB</Modulus>"));
        }

        [Test]
        public static void Verify_PrivateKeyInPlaceOfPublic_IsRefused()
        {
            byte[] d = Data("x");
            string sig = UpdateSigner.Sign(d, TestKeys.PrivA);
            Assert.Equal(SignatureCheck.PrivateKeyEmbedded, UpdateVerifier.Verify(d, sig, TestKeys.PrivA));
        }

        [Test]
        public static void Verify_2048BitKey_IsWeak()
        {
            byte[] d = Data("x");
            string sig;
            using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider())
            {
                rsa.PersistKeyInCsp = false;
                rsa.FromXmlString(TestKeys.Priv2048);
                sig = Convert.ToBase64String(rsa.SignData(d, "SHA256"));
            }
            Assert.Equal(SignatureCheck.WeakKey, UpdateVerifier.Verify(d, sig, UpdateSigner.PublicPart(TestKeys.Priv2048)));
        }

        [Test]
        public static void Signer_RefusesPublicKeyWeakKeyAndEmptyData()
        {
            Assert.Throws<ArgumentException>(delegate { UpdateSigner.Sign(Data("x"), TestKeys.PubA); });
            Assert.Throws<ArgumentException>(delegate { UpdateSigner.Sign(Data("x"), TestKeys.Priv2048); });
            Assert.Throws<ArgumentException>(delegate { UpdateSigner.Sign(new byte[0], TestKeys.PrivA); });
        }

        [Test]
        public static void PublicPart_HasNoPrivateParameters()
        {
            string pub = TestKeys.PubA;
            Assert.True(pub.IndexOf("<" + "D>", StringComparison.Ordinal) < 0, "no D");
            Assert.True(pub.IndexOf("<P>", StringComparison.Ordinal) < 0, "no P");
            Assert.True(UpdateKey.IsConfigured(pub), "configured");
        }

        [Test]
        public static void ShippedKey_PlaceholderCanNeverVerify()
        {
            // While the placeholder is in place nothing verifies; once the real key is pasted
            // this still holds for any signature that was not made by the matching private key.
            string sig = UpdateSigner.Sign(Data("x"), TestKeys.PrivA);
            Assert.True(UpdateVerifier.Verify(Data("x"), sig, UpdateKey.PublicKeyXml) != SignatureCheck.Ok, "throwaway key must not verify");
        }

        [Test]
        public static void IsConfigured_RecognizesPlaceholderAndGarbage()
        {
            Assert.True(!UpdateKey.IsConfigured(UpdateKey.PlaceholderMarker), "marker");
            Assert.True(!UpdateKey.IsConfigured(null), "null");
            Assert.True(!UpdateKey.IsConfigured("  "), "blank");
            Assert.True(!UpdateKey.IsConfigured("hello"), "garbage");
            Assert.True(!UpdateKey.IsConfigured("<RSAKeyValue><Modulus>x</Modulus><<COLE>></RSAKeyValue>"), "marker inside");
        }
    }
}
