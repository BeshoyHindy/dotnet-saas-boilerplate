using System.Security.Cryptography;

namespace Integration.Tests.Infrastructure;

/// <summary>
/// Bytes to upload through the Files flow. Finalize reads an object's first bytes and refuses one
/// that does not carry its declared type's signature (#125), so a test that only needs "a file"
/// uploads random bytes behind a real header rather than bare random bytes.
/// </summary>
public static class UploadPayloads
{
    /// <summary>A <c>%PDF-</c> header followed by random bytes, <paramref name="size"/> bytes in all.</summary>
    public static byte[] Pdf(int size)
    {
        var header = "%PDF-1.7\n"u8;
        ArgumentOutOfRangeException.ThrowIfLessThan(size, header.Length);

        var bytes = new byte[size];
        RandomNumberGenerator.Fill(bytes);
        header.CopyTo(bytes);
        return bytes;
    }
}
