using System.Collections.Frozen;

namespace Boilerplate.Modules.Files.Services;

/// <summary>What <see cref="UploadContentCheck"/> concluded about an upload.</summary>
internal enum UploadContentVerdict
{
    /// <summary>The declared type fits the extension and the bytes carry that type's signature.</summary>
    Match,

    /// <summary>The extension names a type this module has no signature for, so it cannot be verified.</summary>
    UnknownType,

    /// <summary>The declared content type is not one the extension allows.</summary>
    ContentTypeMismatch,

    /// <summary>The bytes do not carry the declared type's signature.</summary>
    SignatureMismatch,
}

/// <summary>
/// Checks an upload's bytes against its declared type (ASVS 5.0 V5.2.2). Without it, finalize
/// only compared the caller's declared <c>Content-Type</c> with the header the same caller sent on
/// the presigned PUT, which proves nothing about the bytes.
/// <para>
/// Every known type is an extension, the media types a browser reports for it, and a signature
/// (the "magic bytes" at the start of the object). A type with no entry here has no known signature
/// and is refused. Text types (<c>.txt</c>, <c>.csv</c>) have no magic bytes, so their signature is
/// the WHATWG MIME Sniffing rule for text: no binary data byte, and nothing a browser would sniff as
/// HTML or XML at the start — storage serves objects without <c>X-Content-Type-Options: nosniff</c>.
/// </para>
/// <para>
/// This is not malware scanning: that stays behind <see cref="IFileScanner"/>, which ships as a
/// no-op. OOXML documents (<c>.docx</c>, <c>.xlsx</c>, <c>.pptx</c>) are ZIP containers, so their
/// signature is the ZIP header and the declared type is what tells them apart.
/// </para>
/// </summary>
internal static class UploadContentCheck
{
    /// <summary>How many leading bytes of an object the check needs to see.</summary>
    public const int PrefixLength = 1024;

    private delegate bool SignatureMatcher(ReadOnlySpan<byte> prefix);

    private sealed record ContentRule(string[] ContentTypes, SignatureMatcher Matches, bool ScriptCapable = false);

    private static readonly FrozenDictionary<string, ContentRule> Rules = BuildRules();

    // WHATWG MIME Sniffing §7.1 "HTML" patterns, each followed by a tag-terminating byte.
    private static readonly byte[][] HtmlSniffPatterns =
    [
        "<!DOCTYPE HTML"u8.ToArray(), "<HTML"u8.ToArray(), "<HEAD"u8.ToArray(), "<SCRIPT"u8.ToArray(),
        "<IFRAME"u8.ToArray(), "<H1"u8.ToArray(), "<DIV"u8.ToArray(), "<FONT"u8.ToArray(),
        "<TABLE"u8.ToArray(), "<A"u8.ToArray(), "<STYLE"u8.ToArray(), "<TITLE"u8.ToArray(),
        "<B"u8.ToArray(), "<BODY"u8.ToArray(), "<BR"u8.ToArray(), "<P"u8.ToArray(),
        "<!--"u8.ToArray(), "<SVG"u8.ToArray(),
    ];

    /// <summary>Whether the extension names a type this module has a signature for.</summary>
    public static bool IsKnownType(string? extension)
        => !string.IsNullOrEmpty(extension) && Rules.ContainsKey(extension);

    /// <summary>
    /// Whether a browser runs script from this type when it is opened inline (SVG, HTML). Such a type
    /// is refused for a category unless the category sets <c>AllowScriptCapableTypes</c>.
    /// </summary>
    public static bool IsScriptCapable(string? extension)
        => !string.IsNullOrEmpty(extension) && Rules.TryGetValue(extension, out var rule) && rule.ScriptCapable;

    /// <summary>
    /// Checks the declaration alone — the type is known and the declared content type is one the
    /// extension allows. Needs no bytes, so it can run before a presigned PUT is minted.
    /// </summary>
    public static UploadContentVerdict VerifyDeclaration(string? extension, string declaredContentType)
    {
        if (string.IsNullOrEmpty(extension) || !Rules.TryGetValue(extension, out var rule))
        {
            return UploadContentVerdict.UnknownType;
        }

        return rule.ContentTypes.Contains(MediaType(declaredContentType), StringComparer.OrdinalIgnoreCase)
            ? UploadContentVerdict.Match
            : UploadContentVerdict.ContentTypeMismatch;
    }

    /// <summary>
    /// Checks the declaration and then the object's first bytes (<paramref name="prefix"/>, up to
    /// <see cref="PrefixLength"/> of them) against the type's signature.
    /// </summary>
    public static UploadContentVerdict Verify(string? extension, string declaredContentType, ReadOnlySpan<byte> prefix)
    {
        var declaration = VerifyDeclaration(extension, declaredContentType);
        if (declaration != UploadContentVerdict.Match)
        {
            return declaration;
        }

        return Rules[extension!].Matches(prefix)
            ? UploadContentVerdict.Match
            : UploadContentVerdict.SignatureMismatch;
    }

    private static FrozenDictionary<string, ContentRule> BuildRules()
    {
        var jpeg = new ContentRule(["image/jpeg"], p => p.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]));
        var html = new ContentRule(["text/html"], IsText, ScriptCapable: true);

        return new Dictionary<string, ContentRule>(StringComparer.OrdinalIgnoreCase)
        {
            [".jpg"] = jpeg,
            [".jpeg"] = jpeg,
            [".png"] = new(["image/png"], p => p.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])),
            [".gif"] = new(["image/gif"], p => p.StartsWith("GIF87a"u8) || p.StartsWith("GIF89a"u8)),
            [".webp"] = new(["image/webp"], p => p.Length >= 12 && p.StartsWith("RIFF"u8) && p.Slice(8, 4).SequenceEqual("WEBP"u8)),
            [".ico"] = new(["image/x-icon", "image/vnd.microsoft.icon"], p => p.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0x01, 0x00])),
            [".pdf"] = new(["application/pdf"], p => p.StartsWith("%PDF-"u8)),
            [".docx"] = new(["application/vnd.openxmlformats-officedocument.wordprocessingml.document"], IsZipWithEntries),
            [".xlsx"] = new(["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"], IsZipWithEntries),
            [".pptx"] = new(["application/vnd.openxmlformats-officedocument.presentationml.presentation"], IsZipWithEntries),
            [".zip"] = new(["application/zip", "application/x-zip-compressed"], p => IsZipWithEntries(p) || p.StartsWith("PK\x05\x06"u8)),
            [".txt"] = new(["text/plain"], IsInertText),
            // Windows reports .csv as application/vnd.ms-excel when Excel is installed.
            [".csv"] = new(["text/csv", "application/vnd.ms-excel"], IsInertText),
            [".svg"] = new(["image/svg+xml"], p => IsText(p) && StartsWithMarkup(p), ScriptCapable: true),
            [".html"] = html,
            [".htm"] = html,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsZipWithEntries(ReadOnlySpan<byte> prefix) => prefix.StartsWith("PK\x03\x04"u8);

    private static string MediaType(string declaredContentType)
    {
        var parameters = declaredContentType.IndexOf(';', StringComparison.Ordinal);
        return (parameters < 0 ? declaredContentType : declaredContentType[..parameters]).Trim();
    }

    /// <summary>Text a browser would not sniff as markup — the only signature plain text has.</summary>
    private static bool IsInertText(ReadOnlySpan<byte> prefix) => IsText(prefix) && !SniffsAsMarkup(prefix);

    /// <summary>Non-empty and free of every WHATWG "binary data byte".</summary>
    private static bool IsText(ReadOnlySpan<byte> prefix)
    {
        if (prefix.IsEmpty)
        {
            return false;
        }

        foreach (var b in prefix)
        {
            if (b <= 0x08 || b == 0x0B || (b >= 0x0E && b <= 0x1A) || (b >= 0x1C && b <= 0x1F))
            {
                return false;
            }
        }
        return true;
    }

    private static bool StartsWithMarkup(ReadOnlySpan<byte> prefix)
    {
        var body = SkipBomAndWhitespace(prefix);
        return !body.IsEmpty && body[0] == (byte)'<';
    }

    private static bool SniffsAsMarkup(ReadOnlySpan<byte> prefix)
    {
        var body = SkipBomAndWhitespace(prefix);
        if (body.StartsWith("<?xml"u8))
        {
            return true;
        }

        foreach (var pattern in HtmlSniffPatterns)
        {
            if (body.Length > pattern.Length
                && StartsWithIgnoreAsciiCase(body, pattern)
                && IsTagTerminator(body[pattern.Length]))
            {
                return true;
            }
        }
        return false;
    }

    private static ReadOnlySpan<byte> SkipBomAndWhitespace(ReadOnlySpan<byte> prefix)
    {
        if (prefix.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            prefix = prefix[3..];
        }

        var start = 0;
        while (start < prefix.Length && prefix[start] is 0x09 or 0x0A or 0x0C or 0x0D or 0x20)
        {
            start++;
        }
        return prefix[start..];
    }

    private static bool StartsWithIgnoreAsciiCase(ReadOnlySpan<byte> body, ReadOnlySpan<byte> upperPattern)
    {
        for (var i = 0; i < upperPattern.Length; i++)
        {
            var b = body[i];
            var upper = b is >= (byte)'a' and <= (byte)'z' ? (byte)(b - 0x20) : b;
            if (upper != upperPattern[i])
            {
                return false;
            }
        }
        return true;
    }

    // WHATWG's tag-terminating bytes are space and '>'; whitespace and '/' are refused as well.
    private static bool IsTagTerminator(byte b) => b is 0x20 or 0x3E or 0x09 or 0x0A or 0x0C or 0x0D or 0x2F;
}
