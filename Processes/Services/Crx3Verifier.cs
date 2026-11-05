using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace TabloidBrowser;

/// <summary>
/// Verifies CRX3 containers the way the Chrome Web Store produces them:
/// <list type="bullet">
/// <item>parses the Cr24 container (magic, version 3, header length),</item>
/// <item>extracts the signed header data and the asymmetric key proofs,</item>
/// <item>requires at least one cryptographic signature over the signed header
/// data to verify (ECDSA with SHA-256 or RSA PKCS#1 with SHA-256),</item>
/// <item>derives the extension identity from the crx_id stored inside the
/// signed data â€” the extension id is exactly a base-16 ("a"-"p") encoding of
/// those bytes, so a valid signature plus an id match proves the package is
/// the one the store publishes for that extension id.</item>
/// </list>
/// It also rejects ZIP payloads that declare implausible sizes (zip bombs)
/// before anything is extracted.
/// </summary>
public static class Crx3Verifier
{
    private const int HeaderLengthOffset = 8;
    private const int HeaderStart = 12;
    private const long DefaultMaxTotalUncompressedBytes = 512L * 1024 * 1024;
    private const long DefaultMaxSingleEntryBytes = 256L * 1024 * 1024;
    private const int DefaultMaxZipEntries = 20_000;

    public sealed record Verification(
        string VerifiedExtensionId,
        int ZipStart,
        int EntryCount,
        long TotalUncompressedBytes);

    /// <summary>
    /// Verifies a CRX3 container. When <paramref name="expectedExtensionId"/> is
    /// supplied, the signed crx_id must map to it.
    /// </summary>
    public static bool TryVerify(
        byte[] data,
        string? expectedExtensionId,
        out Verification? verification,
        out string? error)
    {
        verification = null;
        error = null;

        if (data is null || data.Length < HeaderStart)
        {
            error = "File is too small to be a CRX container.";
            return false;
        }

        if (data[0] != 'C' || data[1] != 'r' || data[2] != '2' || data[3] != '4')
        {
            error = "Not a CRX3 container (Cr24 magic missing).";
            return false;
        }

        var version = BitConverter.ToUInt32(data, 4);
        if (version != 3)
        {
            error = $"Unsupported CRX version {version}.";
            return false;
        }

        var headerLength = BitConverter.ToUInt32(data, HeaderLengthOffset);
        if (headerLength == 0 || headerLength > 4 * 1024 * 1024 ||
            HeaderStart + headerLength > data.Length)
        {
            error = "Corrupt CRX3 header.";
            return false;
        }

        var zipStart = HeaderStart + (int)headerLength;
        if (zipStart + 4 > data.Length)
        {
            error = "CRX3 container has no ZIP payload.";
            return false;
        }

        // ---- header protobuf: field 2 = signed header data, field 10000 = proofs
        if (!TryReadFields(data, HeaderStart, (int)headerLength, out var headerFields, out error))
        {
            return false;
        }

        if (!headerFields.TryGetValue(2, out var signedDataSpans) || signedDataSpans.Count == 0)
        {
            error = "CRX3 header has no signed data.";
            return false;
        }

        if (!headerFields.TryGetValue(10_000, out var proofSpans) || proofSpans.Count == 0)
        {
            error = "CRX3 header has no signature proofs.";
            return false;
        }

        var signedHeaderData = data.AsSpan(signedDataSpans[0].Offset, signedDataSpans[0].Length).ToArray();

        // ---- signed data protobuf: field 1 = crx_id
        if (!TryReadFields(signedHeaderData, 0, signedHeaderData.Length, out var signedFields, out error))
        {
            return false;
        }

        if (!signedFields.TryGetValue(1, out var crxIdSpans) ||
            crxIdSpans.Count == 0 ||
            crxIdSpans[0].Length < 16)
        {
            error = "CRX3 signed data has no crx_id.";
            return false;
        }

        var crxId = new byte[16];
        Array.Copy(signedHeaderData, crxIdSpans[0].Offset, crxId, 0, 16);
        var verifiedExtensionId = ExtensionIdFromCrxId(crxId);

        if (expectedExtensionId is not null &&
            !string.Equals(verifiedExtensionId, expectedExtensionId, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Extension identity mismatch: signed as '{verifiedExtensionId}', " +
                    $"expected '{expectedExtensionId}'.";
            return false;
        }

        // ---- at least one proof must cryptographically verify
        var anyProofValid = proofSpans.Any(proofSpan =>
        {
            if (!TryReadFields(data, proofSpan.Offset, proofSpan.Length, out var proofFields, out _))
            {
                return false;
            }

            if (!proofFields.TryGetValue(1, out var keySpans) || keySpans.Count == 0 ||
                !proofFields.TryGetValue(2, out var signatureSpans) || signatureSpans.Count == 0)
            {
                return false;
            }

            var publicKey = data.AsSpan(keySpans[0].Offset, keySpans[0].Length).ToArray();
            var signature = data.AsSpan(signatureSpans[0].Offset, signatureSpans[0].Length).ToArray();
            return VerifyProof(publicKey, signature, signedHeaderData);
        });

        if (!anyProofValid)
        {
            error = "CRX3 signature verification failed.";
            return false;
        }

        // ---- sanity-check the declared ZIP payload before extraction
        try
        {
            using var zipStream = new MemoryStream(data, zipStart, data.Length - zipStart);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

            if (archive.Entries.Count > DefaultMaxZipEntries)
            {
                error = $"CRX payload declares {archive.Entries.Count} entries (limit {DefaultMaxZipEntries}).";
                return false;
            }

            long total = 0;
            foreach (var entry in archive.Entries)
            {
                total += entry.Length;
                if (entry.Length > DefaultMaxSingleEntryBytes)
                {
                    error = $"CRX payload entry '{entry.FullName}' declares {entry.Length} bytes " +
                            $"(limit {DefaultMaxSingleEntryBytes}).";
                    return false;
                }
            }

            if (total > DefaultMaxTotalUncompressedBytes)
            {
                error = $"CRX payload declares {total} uncompressed bytes (limit {DefaultMaxTotalUncompressedBytes}).";
                return false;
            }

            verification = new Verification(verifiedExtensionId, zipStart, archive.Entries.Count, total);
            return true;
        }
        catch (InvalidDataException ex)
        {
            error = $"CRX payload is not a readable ZIP archive: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Maps a 16-byte crx_id to its 32-character extension id
    /// (each nibble rendered as "a" + value, Chrome's id encoding).
    /// </summary>
    public static string ExtensionIdFromCrxId(ReadOnlySpan<byte> crxId)
    {
        var builder = new StringBuilder(crxId.Length * 2);
        foreach (var b in crxId)
        {
            builder.Append((char)('a' + (b >> 4)));
            builder.Append((char)('a' + (b & 0x0F)));
        }

        return builder.ToString();
    }

    private static bool VerifyProof(byte[] publicKey, byte[] signature, byte[] signedData)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKey, out _);

            // Producers use both DER (RFC 3279) and fixed-size IEEE P1363
            // encodings; the default VerifyData encoding has also changed
            // across runtimes, so every combination is tried.
            if (ecdsa.VerifyData(signedData, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence) ||
                ecdsa.VerifyData(signedData, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                return true;
            }

            if (signature.Length is 64 or 96)
            {
                var der = EncodeEcdsaDerSignature(signature);
                if (ecdsa.VerifyData(signedData, der, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidOperationException or ArgumentException or PlatformNotSupportedException)
        {
            // Not an ECDSA key â€” try RSA below.
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(publicKey, out _);
            return rsa.VerifyData(
                signedData,
                signature,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidOperationException or ArgumentException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    internal static byte[] EncodeEcdsaDerSignature(byte[] raw)
    {
        var half = raw.Length / 2;
        var body = EncodeDerInteger(raw.AsSpan(0, half))
            .Concat(EncodeDerInteger(raw.AsSpan(half, half)))
            .ToArray();
        return EncodeDer(0x30, body);
    }

    private static byte[] EncodeDerInteger(ReadOnlySpan<byte> unsignedMagnitude)
    {
        var start = 0;
        while (start < unsignedMagnitude.Length - 1 && unsignedMagnitude[start] == 0)
        {
            start++;
        }

        var magnitude = unsignedMagnitude[start..];
        var needsPad = (magnitude[0] & 0x80) != 0;
        var content = new byte[magnitude.Length + (needsPad ? 1 : 0)];
        if (needsPad)
        {
            content[0] = 0;
        }

        magnitude.CopyTo(content.AsSpan(needsPad ? 1 : 0));
        return EncodeDer(0x02, content);
    }

    private static byte[] EncodeDer(byte tag, byte[] content)
    {
        var lengthBytes = content.Length < 128
            ? [(byte)content.Length]
            : EncodeLongLength(content.Length);
        var result = new byte[1 + lengthBytes.Length + content.Length];
        result[0] = tag;
        lengthBytes.CopyTo(result, 1);
        content.CopyTo(result, 1 + lengthBytes.Length);
        return result;
    }

    private static byte[] EncodeLongLength(int length)
    {
        var bytes = new List<byte>();
        while (length > 0)
        {
            bytes.Insert(0, (byte)(length & 0xFF));
            length >>= 8;
        }

        return [(byte)(0x80 | bytes.Count), .. bytes];
    }

    private readonly record struct FieldSpan(int Offset, int Length);

    private static bool TryReadFields(
        byte[] data,
        int offset,
        int length,
        out Dictionary<int, List<FieldSpan>> fields,
        out string? error)
    {
        fields = [];
        error = null;

        var pos = offset;
        var end = offset + length;
        while (pos < end)
        {
            if (!TryReadVarint(data, ref pos, end, out var key))
            {
                error = "Malformed protobuf field key.";
                return false;
            }

            var fieldNumber = (int)(key >> 3);
            var wireType = (int)(key & 0x7);
            if (fieldNumber <= 0)
            {
                error = "Malformed protobuf field number.";
                return false;
            }

            switch (wireType)
            {
                case 0: // varint
                    if (!TryReadVarint(data, ref pos, end, out _))
                    {
                        error = "Malformed protobuf varint value.";
                        return false;
                    }

                    break;

                case 1: // 64-bit
                    if (pos + 8 > end)
                    {
                        error = "Truncated protobuf 64-bit field.";
                        return false;
                    }

                    pos += 8;
                    break;

                case 2: // length-delimited
                    if (!TryReadVarint(data, ref pos, end, out var fieldLength) ||
                        fieldLength < 0 ||
                        pos + fieldLength > end)
                    {
                        error = "Malformed protobuf length-delimited field.";
                        return false;
                    }

                    if (!fields.TryGetValue(fieldNumber, out var spans))
                    {
                        spans = [];
                        fields[fieldNumber] = spans;
                    }

                    spans.Add(new FieldSpan(pos, (int)fieldLength));
                    pos += (int)fieldLength;
                    break;

                case 5: // 32-bit
                    if (pos + 4 > end)
                    {
                        error = "Truncated protobuf 32-bit field.";
                        return false;
                    }

                    pos += 4;
                    break;

                default:
                    error = "Unsupported protobuf wire type.";
                    return false;
            }
        }

        return true;
    }

    private static bool TryReadVarint(byte[] data, ref int pos, int end, out long value)
    {
        value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (pos >= end)
            {
                return false;
            }

            var b = data[pos++];
            value |= (long)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return value >= 0;
            }
        }

        return false;
    }
}
