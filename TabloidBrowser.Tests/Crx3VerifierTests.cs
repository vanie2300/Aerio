using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace TabloidBrowser.Tests;

public class Crx3VerifierTests
{
    private const int FakeZipBytes = 4096;

    [Fact]
    public void Verify_EcdsaSignedCrx3WithMatchingId_Succeeds()
    {
        var crxId = RandomNumberGenerator.GetBytes(16);
        var expectedId = Crx3Verifier.ExtensionIdFromCrxId(crxId);
        var data = BuildCrx3(crxId, ECDsa.Create(ECCurve.NamedCurves.nistP256));

        var ok = Crx3Verifier.TryVerify(data, expectedId, out var verification, out var error);

        Assert.True(ok, error);
        Assert.Equal(expectedId, verification!.VerifiedExtensionId);
        Assert.Equal(expectedId.Length, verification.VerifiedExtensionId.Length);
        Assert.All(verification.VerifiedExtensionId, c => Assert.InRange(c, 'a', 'p'));
        Assert.Equal(12 + GetHeaderLength(data), verification.ZipStart);
        Assert.True(verification.EntryCount >= 1);
        Assert.True(verification.TotalUncompressedBytes > 0);
    }

    [Fact]
    public void Verify_RsaSignedCrx3_Succeeds()
    {
        var crxId = RandomNumberGenerator.GetBytes(16);
        var expectedId = Crx3Verifier.ExtensionIdFromCrxId(crxId);
        var data = BuildCrx3(crxId, RSA.Create(2048));

        var ok = Crx3Verifier.TryVerify(data, expectedId, out var verification, out var error);

        Assert.True(ok, error);
        Assert.Equal(expectedId, verification!.VerifiedExtensionId);
    }

    [Fact]
    public void Verify_RawP1363EcdsaSignature_Succeeds()
    {
        var crxId = RandomNumberGenerator.GetBytes(16);
        var expectedId = Crx3Verifier.ExtensionIdFromCrxId(crxId);
        var data = BuildCrx3(crxId, ECDsa.Create(ECCurve.NamedCurves.nistP256), rawSignature: true);

        var ok = Crx3Verifier.TryVerify(data, expectedId, out var verification, out var error);

        Assert.True(ok, error);
        Assert.Equal(expectedId, verification!.VerifiedExtensionId);
    }

    [Fact]
    public void Verify_TamperedSignature_Fails()
    {
        var crxId = RandomNumberGenerator.GetBytes(16);
        var data = BuildCrx3(crxId, ECDsa.Create(ECCurve.NamedCurves.nistP256));

        // Flip a byte somewhere after the header (inside the ZIP payload) and
        // also flip one signature byte inside the header.
        data[data.Length - 1] ^= 0xFF;
        var headerLength = GetHeaderLength(data);
        data[12 + headerLength / 2] ^= 0x01;

        var ok = Crx3Verifier.TryVerify(data, null, out _, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void Verify_WrongExpectedId_Fails()
    {
        var crxId = RandomNumberGenerator.GetBytes(16);
        var data = BuildCrx3(crxId, ECDsa.Create(ECCurve.NamedCurves.nistP256));

        var ok = Crx3Verifier.TryVerify(data, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", out _, out var error);

        Assert.False(ok);
        Assert.Contains("identity mismatch", error);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void Verify_UnsupportedVersion_Fails(uint version)
    {
        var data = BuildCrx3(RandomNumberGenerator.GetBytes(16), ECDsa.Create(ECCurve.NamedCurves.nistP256));
        BitConverter.GetBytes(version).CopyTo(data, 4);

        var ok = Crx3Verifier.TryVerify(data, null, out _, out var error);

        Assert.False(ok);
        Assert.Contains("version", error);
    }

    [Fact]
    public void Verify_NoSignatureProofs_Fails()
    {
        // Signed header data present, but the proofs field (10000) is omitted.
        var crxId = RandomNumberGenerator.GetBytes(16);
        var signedData = ProtoBytes(1, crxId);
        var header = ProtoBytes(2, signedData);
        var zip = CreateZip();
        var data = BuildContainer(version: 3, header, zip);

        var ok = Crx3Verifier.TryVerify(data, null, out _, out var error);

        Assert.False(ok);
        Assert.Contains("no signature proofs", error);
    }

    [Fact]
    public void Verify_ZipBombEntry_Fails()
    {
        var crxId = RandomNumberGenerator.GetBytes(16);
        var header = BuildHeader(crxId, ECDsa.Create(ECCurve.NamedCurves.nistP256), out _);
        var zip = CreateZip(longEntryBytes: 600L * 1024 * 1024);
        var data = BuildContainer(version: 3, header, zip);

        var ok = Crx3Verifier.TryVerify(data, null, out _, out var error);

        Assert.False(ok);
        Assert.Contains("declares", error);
    }

    [Fact]
    public void ExtensionIdFromCrxId_KnownBytes_ProduceExpectedId()
    {
        // 0x00 -> "aa", 0x0F -> "ap", 0x1A -> "bk", 0xFF -> "pp"
        var id = Crx3Verifier.ExtensionIdFromCrxId([0x00, 0x0F, 0x1A, 0xFF]);

        Assert.Equal("aaapbkpp", id);
    }

    // ---------- helpers ----------

    private static int GetHeaderLength(byte[] data) => (int)BitConverter.ToUInt32(data, 8);

    private static byte[] BuildCrx3(byte[] crxId, object signer, bool rawSignature = false) =>
        BuildContainer(version: 3, BuildHeader(crxId, signer, out var signedData, rawSignature), CreateZip());

    private static byte[] BuildHeader(byte[] crxId, object signer, out byte[] signedData, bool rawSignature = false)
    {
        signedData = ProtoBytes(1, crxId);

        byte[] publicKey;
        byte[] signature;
        switch (signer)
        {
            case ECDsa ecdsa:
                publicKey = ecdsa.ExportSubjectPublicKeyInfo();
                signature = rawSignature
                    ? ecdsa.SignData(signedData, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
                    : ecdsa.SignData(signedData, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
                break;
            case RSA rsa:
                publicKey = rsa.ExportSubjectPublicKeyInfo();
                signature = rsa.SignData(signedData, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                break;
            default:
                throw new ArgumentException("Unknown signer", nameof(signer));
        }

        var proof = ProtoBytes(1, publicKey).Concat(ProtoBytes(2, signature)).ToArray();
        // Field 10000, wire type 2: key varint = (10000 << 3) | 2
        var header = ProtoBytes(2, signedData).Concat(ProtoBytes(10_000, proof)).ToArray();
        return header;
    }

    private static byte[] BuildContainer(uint version, byte[] header, byte[] zip)
    {
        var data = new byte[12 + header.Length + zip.Length];
        data[0] = (byte)'C';
        data[1] = (byte)'r';
        data[2] = (byte)'2';
        data[3] = (byte)'4';
        BitConverter.GetBytes(version).CopyTo(data, 4);
        BitConverter.GetBytes((uint)header.Length).CopyTo(data, 8);
        header.CopyTo(data, 12);
        zip.CopyTo(data, 12 + header.Length);
        return data;
    }

    private static byte[] ProtoBytes(int fieldNumber, byte[] payload)
    {
        var key = (long)(fieldNumber << 3) | 2;
        return EncodeVarint(key).Concat(EncodeVarint(payload.Length)).Concat(payload).ToArray();
    }

    private static byte[] EncodeVarint(long value)
    {
        var bytes = new List<byte>();
        while (value > 0x7F)
        {
            bytes.Add((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }

        bytes.Add((byte)value);
        return [.. bytes];
    }

    private static byte[] CreateZip(long longEntryBytes = 0)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("manifest.json");
            using (var writer = new StreamWriter(entry.Open()))
            {
                writer.Write("""{"name":"test"}""");
            }

            var padding = archive.CreateEntry("padding.bin");
            using (var paddingWriter = new BinaryWriter(padding.Open()))
            {
                paddingWriter.Write(new byte[longEntryBytes > 0 ? longEntryBytes : FakeZipBytes]);
            }
        }

        return stream.ToArray();
    }
}
