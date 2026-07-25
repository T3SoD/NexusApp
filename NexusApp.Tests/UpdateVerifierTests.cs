using System.Security.Cryptography;
using System.Text;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// The whole trust chain reduces to these functions, so they are tested with a real
// in-test keypair and hostile inputs. Everything must fail CLOSED: bad key, bad sig,
// bad hex, bad version all return false, never throw.
public class UpdateVerifierTests
{
    private static (string privPem, string pubPem) NewKeyPair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (ecdsa.ExportPkcs8PrivateKeyPem(), ecdsa.ExportSubjectPublicKeyInfoPem());
    }

    private static byte[] Sign(string privPem, byte[] data)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(privPem);
        return ecdsa.SignData(data, HashAlgorithmName.SHA256);
    }

    [Fact]
    public void VerifySignature_ValidSignature_IsAccepted()
    {
        var (priv, pub) = NewKeyPair();
        var data = Encoding.UTF8.GetBytes("manifest bytes");
        Assert.True(UpdateVerifier.VerifySignature(data, Sign(priv, data), pub));
    }

    [Fact]
    public void VerifySignature_TamperedData_IsRejected()
    {
        var (priv, pub) = NewKeyPair();
        var data = Encoding.UTF8.GetBytes("manifest bytes");
        var sig = Sign(priv, data);
        data[0] ^= 0x01;
        Assert.False(UpdateVerifier.VerifySignature(data, sig, pub));
    }

    [Fact]
    public void VerifySignature_WrongKey_IsRejected()
    {
        var (priv, _) = NewKeyPair();
        var (_, otherPub) = NewKeyPair();
        var data = Encoding.UTF8.GetBytes("manifest bytes");
        Assert.False(UpdateVerifier.VerifySignature(data, Sign(priv, data), otherPub));
    }

    [Theory]
    [InlineData(0)]    // empty signature
    [InlineData(10)]   // truncated signature
    public void VerifySignature_MalformedSignature_IsRejectedNotThrown(int len)
    {
        var (_, pub) = NewKeyPair();
        Assert.False(UpdateVerifier.VerifySignature(Encoding.UTF8.GetBytes("x"), new byte[len], pub));
    }

    [Fact]
    public void VerifySignature_DerEncodedSignature_IsRejected()
    {
        // Pins the wire format: verification uses the default IEEE P1363 (raw r||s) encoding, so
        // a DER/Rfc3279 signature over the SAME bytes with the SAME key must not verify. The
        // signing script produces P1363; if that ever drifts to DER, this test catches it here
        // instead of in the field, where every client would just report a failed check.
        var (priv, pub) = NewKeyPair();
        var data = Encoding.UTF8.GetBytes("manifest bytes");
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(priv);
        var derSig = ecdsa.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        Assert.False(UpdateVerifier.VerifySignature(data, derSig, pub));
    }

    [Fact]
    public void VerifySignature_GarbagePem_IsRejectedNotThrown() =>
        Assert.False(UpdateVerifier.VerifySignature(new byte[] { 1 }, new byte[64], "not a pem"));

    [Fact]
    public void VerifySignature_ProductionOverload_FailsClosedWithGarbageInput() =>
        // With the shipped key (placeholder until the ceremony, real after), garbage
        // must simply be false. This test never depends on WHICH key is compiled in.
        Assert.False(UpdateVerifier.VerifySignature(new byte[] { 1, 2, 3 }, new byte[] { 4, 5, 6 }));

    [Fact]
    public void FileHashMatches_MatchAndMismatch()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes("payload"));
            var hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("payload"))).ToLowerInvariant();
            Assert.True(UpdateVerifier.FileHashMatches(path, hex));
            Assert.False(UpdateVerifier.FileHashMatches(path, hex.Replace(hex[0], hex[0] == 'a' ? 'b' : 'a')));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nothex")]
    public void HashMatches_BadExpectedHex_IsFalse(string? hex) =>
        Assert.False(UpdateVerifier.HashMatches(hex, SHA256.HashData(new byte[] { 1 })));

    [Theory]
    [InlineData("6.6.2", "6.7.0", true)]
    [InlineData("6.6.2", "7.0.0", true)]
    [InlineData("6.6.2", "6.6.2", false)]   // same version: never reinstall
    [InlineData("6.6.2", "6.6.1", false)]   // downgrade: refused
    [InlineData("6.6.2", "banana", false)]
    [InlineData("banana", "6.7.0", false)]
    [InlineData(null, "6.7.0", false)]
    [InlineData("6.6.2", null, false)]
    public void IsUpgrade_Matrix(string? current, string? candidate, bool expected) =>
        Assert.Equal(expected, UpdateVerifier.IsUpgrade(current, candidate));

    [Fact]
    public void PublicKeyPem_IsARealP256Key()
    {
        // Guards against shipping the placeholder (or a corrupted paste): the compiled-in
        // key must import as a P-256 public key. This test is added only after the ceremony.
        using var ecdsa = System.Security.Cryptography.ECDsa.Create();
        ecdsa.ImportFromPem(UpdateVerifier.PublicKeyPem);
        Assert.Equal(256, ecdsa.KeySize);
    }
}
