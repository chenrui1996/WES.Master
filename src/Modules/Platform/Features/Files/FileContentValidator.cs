using System.Net.Http.Headers;
using System.Text;
using Wes.Platform.Domain;

namespace Wes.Platform.Features.Files;

/// <summary>Stable result categories for bounded file declaration and content checks.</summary>
public enum FileContentRejection
{
    /// <summary>File metadata and basic content markers agree with an allowed format.</summary>
    None,
    /// <summary>The file contains no bytes.</summary>
    Empty,
    /// <summary>The file exceeds the approved byte limit.</summary>
    TooLarge,
    /// <summary>The name is empty, contains a path separator, or contains a control character.</summary>
    InvalidFileName,
    /// <summary>The file extension is not in the approved format allowlist.</summary>
    UnsupportedExtension,
    /// <summary>The declared media type cannot be parsed or has unsupported parameters.</summary>
    InvalidMediaType,
    /// <summary>The media type does not match the allowed type for the extension.</summary>
    MediaTypeMismatch,
    /// <summary>The content begins with a recognized executable, macro-document, or archive signature.</summary>
    ForbiddenSignature,
    /// <summary>The content marker does not match the declared allowed format.</summary>
    SignatureMismatch,
    /// <summary>The plain-text content is not valid UTF-8.</summary>
    InvalidUtf8,
    /// <summary>The plain-text content contains a NUL byte.</summary>
    ContainsNul,
    /// <summary>The plain-text content contains a recognized active markup or script marker.</summary>
    DisallowedMarkup
}

/// <summary>Content validation result that intentionally contains no submitted metadata or file bytes.</summary>
public sealed record FileContentValidationResult(FileContentRejection Rejection)
{
    /// <summary>Whether the basic format and size checks accepted the submitted bytes.</summary>
    public bool IsAccepted => Rejection == FileContentRejection.None;
}

/// <summary>Performs deterministic PAR-04 size, media declaration, and basic content marker checks.</summary>
/// <remarks>
/// Acceptance is not malware clearance or deep format validation. Callers must keep data quarantined until
/// configured storage and scanning steps independently succeed.
/// </remarks>
public static class FileContentValidator
{
    /// <summary>Maximum permitted file size in bytes.</summary>
    public const int MaximumBytes = 20 * 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JpegSignature = [0xFF, 0xD8, 0xFF];
    private static readonly byte[][] ForbiddenSignatures =
    [
        [0x4D, 0x5A],
        [0x7F, 0x45, 0x4C, 0x46],
        [0xFE, 0xED, 0xFA, 0xCE],
        [0xFE, 0xED, 0xFA, 0xCF],
        [0xCE, 0xFA, 0xED, 0xFE],
        [0xCF, 0xFA, 0xED, 0xFE],
        [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1],
        [0x50, 0x4B, 0x03, 0x04],
        [0x50, 0x4B, 0x05, 0x06],
        [0x50, 0x4B, 0x07, 0x08],
        [0x1F, 0x8B, 0x08],
        [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07],
        [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C]
    ];

    private static readonly string[] DisallowedTextMarkers =
    [
        "<!doctype html",
        "<html",
        "<script",
        "<svg",
        "<iframe",
        "<object",
        "<embed",
        "<?xml",
        "<?php",
        "<%",
        "javascript:",
        "vbscript:"
    ];

    /// <summary>Validates one already bounded byte buffer without storing, logging, or transforming its content.</summary>
    public static FileContentValidationResult Validate(string fileName, string declaredContentType, ReadOnlyMemory<byte> content)
    {
        if (content.Length > MaximumBytes) return Reject(FileContentRejection.TooLarge);
        if (content.IsEmpty) return Reject(FileContentRejection.Empty);
        if (!QuarantinedFile.IsValidDisplayName(fileName))
            return Reject(FileContentRejection.InvalidFileName);

        var extension = Path.GetExtension(fileName);
        var expectedType = extension.ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".txt" => "text/plain",
            _ => null
        };
        if (expectedType is null) return Reject(FileContentRejection.UnsupportedExtension);

        if (!MediaTypeHeaderValue.TryParse(declaredContentType, out var mediaType))
            return Reject(FileContentRejection.InvalidMediaType);
        if (!string.Equals(mediaType.MediaType, expectedType, StringComparison.OrdinalIgnoreCase))
            return Reject(FileContentRejection.MediaTypeMismatch);
        if (!HasAllowedParameters(mediaType, expectedType == "text/plain"))
            return Reject(FileContentRejection.InvalidMediaType);

        var bytes = content.Span;
        foreach (var signature in ForbiddenSignatures)
        {
            if (bytes.StartsWith(signature)) return Reject(FileContentRejection.ForbiddenSignature);
        }

        return expectedType switch
        {
            "application/pdf" => StartsWith(bytes, "%PDF-"u8)
                ? Accept() : Reject(FileContentRejection.SignatureMismatch),
            "image/png" => bytes.StartsWith(PngSignature)
                ? Accept() : Reject(FileContentRejection.SignatureMismatch),
            "image/jpeg" => bytes.StartsWith(JpegSignature)
                ? Accept() : Reject(FileContentRejection.SignatureMismatch),
            "text/plain" => ValidateText(bytes),
            _ => Reject(FileContentRejection.UnsupportedExtension)
        };
    }

    private static FileContentValidationResult ValidateText(ReadOnlySpan<byte> content)
    {
        if (content.Contains((byte)0)) return Reject(FileContentRejection.ContainsNul);
        string text;
        try
        {
            text = StrictUtf8.GetString(content);
        }
        catch (DecoderFallbackException)
        {
            return Reject(FileContentRejection.InvalidUtf8);
        }

        return DisallowedTextMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase))
            || text.TrimStart('\uFEFF').TrimStart().StartsWith("#!", StringComparison.Ordinal)
                ? Reject(FileContentRejection.DisallowedMarkup)
                : Accept();
    }

    private static bool HasAllowedParameters(MediaTypeHeaderValue mediaType, bool isText)
    {
        var parameters = mediaType.Parameters.ToArray();
        if (!isText) return parameters.Length == 0;
        if (parameters.Length == 0) return true;
        return parameters.Length == 1
            && string.Equals(parameters[0].Name, "charset", StringComparison.OrdinalIgnoreCase)
            && string.Equals(mediaType.CharSet?.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase);
    }

    private static bool StartsWith(ReadOnlySpan<byte> content, ReadOnlySpan<byte> signature) =>
        content.StartsWith(signature);

    private static FileContentValidationResult Accept() => new(FileContentRejection.None);
    private static FileContentValidationResult Reject(FileContentRejection reason) => new(reason);
}
