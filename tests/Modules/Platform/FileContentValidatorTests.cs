using System.Text;
using Wes.Platform.Features.Files;
using Xunit;

namespace Wes.Modules.Tests.Platform;

public sealed class FileContentValidatorTests
{
    private const int MaximumBytes = 20 * 1024 * 1024;

    [Theory]
    [InlineData("document.pdf", "application/pdf")]
    [InlineData("image.png", "image/png")]
    [InlineData("image.jpg", "image/jpeg")]
    [InlineData("image.jpeg", "image/jpeg")]
    [InlineData("notes.txt", "text/plain")]
    [InlineData("notes.txt", "text/plain; charset=utf-8")]
    public void Validate_AcceptsSupportedFormats(string fileName, string contentType)
    {
        var result = FileContentValidator.Validate(fileName, contentType, ValidContent(fileName));

        Assert.True(result.IsAccepted);
        Assert.Equal(FileContentRejection.None, result.Rejection);
    }

    [Fact]
    public void Validate_AcceptsMaximumFileSizeAndRejectsOneByteMore()
    {
        var maximum = new byte[MaximumBytes];
        Encoding.ASCII.GetBytes("%PDF-1.7").CopyTo(maximum, 0);
        var tooLarge = new byte[MaximumBytes + 1];
        Encoding.ASCII.GetBytes("%PDF-1.7").CopyTo(tooLarge, 0);

        Assert.Equal(FileContentRejection.None,
            FileContentValidator.Validate("document.pdf", "application/pdf", maximum).Rejection);
        Assert.Equal(FileContentRejection.TooLarge,
            FileContentValidator.Validate("document.pdf", "application/pdf", tooLarge).Rejection);
    }

    [Fact]
    public void Validate_RejectsEmptyContent()
    {
        var result = FileContentValidator.Validate("empty.txt", "text/plain", ReadOnlyMemory<byte>.Empty);

        Assert.Equal(FileContentRejection.Empty, result.Rejection);
    }

    [Theory]
    [InlineData("document.pdf", "image/png", "%PDF-1.7")]
    [InlineData("document.png", "application/pdf", "%PDF-1.7")]
    [InlineData("document.pdf", "application/pdf", "not-a-pdf")]
    [InlineData("image.png", "image/png", "not-a-png")]
    [InlineData("image.jpg", "image/jpeg", "not-a-jpeg")]
    public void Validate_RejectsMismatchedExtensionMediaTypeOrSignature(string fileName, string contentType, string content)
    {
        var result = FileContentValidator.Validate(fileName, contentType, Encoding.UTF8.GetBytes(content));

        Assert.NotEqual(FileContentRejection.None, result.Rejection);
    }

    [Theory]
    [InlineData("document.exe", "application/pdf")]
    [InlineData("folder/document.pdf", "application/pdf")]
    [InlineData("folder\\document.pdf", "application/pdf")]
    [InlineData("document\u0001.pdf", "application/pdf")]
    public void Validate_RejectsUnsafeOrUnsupportedNames(string fileName, string contentType)
    {
        var result = FileContentValidator.Validate(fileName, contentType, Encoding.ASCII.GetBytes("%PDF-1.7"));

        Assert.NotEqual(FileContentRejection.None, result.Rejection);
    }

    [Theory]
    [InlineData("notes.txt", "text/plain; charset=iso-8859-1")]
    [InlineData("notes.txt", "text/plain; format=flowed")]
    [InlineData("document.pdf", "application/pdf; charset=utf-8")]
    [InlineData("document.pdf", "not a media type")]
    public void Validate_RejectsInvalidMediaTypesAndParameters(string fileName, string contentType)
    {
        var result = FileContentValidator.Validate(fileName, contentType, Encoding.UTF8.GetBytes("%PDF-1.7"));

        Assert.NotEqual(FileContentRejection.None, result.Rejection);
    }

    [Fact]
    public void Validate_RejectsInvalidUtf8AndNulText()
    {
        Assert.Equal(FileContentRejection.InvalidUtf8,
            FileContentValidator.Validate("notes.txt", "text/plain", new byte[] { 0xC3, 0x28 }).Rejection);
        Assert.Equal(FileContentRejection.ContainsNul,
            FileContentValidator.Validate("notes.txt", "text/plain", new byte[] { 0x41, 0x00, 0x42 }).Rejection);
    }

    [Theory]
    [InlineData("<html><body>x</body></html>")]
    [InlineData("<!DOCTYPE html><html></html>")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>")]
    [InlineData("<?xml version=\"1.0\"?><svg></svg>")]
    [InlineData("#!/bin/sh\necho unsafe")]
    [InlineData("\uFEFF#!/bin/sh\necho unsafe")]
    [InlineData("javascript:alert(1)")]
    public void Validate_RejectsActiveMarkupInPlainText(string content)
    {
        var result = FileContentValidator.Validate("notes.txt", "text/plain", Encoding.UTF8.GetBytes(content));

        Assert.Equal(FileContentRejection.DisallowedMarkup, result.Rejection);
    }

    [Theory]
    [InlineData(new byte[] { 0x4D, 0x5A })]
    [InlineData(new byte[] { 0x7F, 0x45, 0x4C, 0x46 })]
    [InlineData(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 })]
    [InlineData(new byte[] { 0x50, 0x4B, 0x03, 0x04 })]
    [InlineData(new byte[] { 0x1F, 0x8B, 0x08 })]
    [InlineData(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 })]
    [InlineData(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C })]
    public void Validate_RejectsExecutableOfficeAndArchiveSignatures(byte[] content)
    {
        var result = FileContentValidator.Validate("disguised.pdf", "application/pdf", content);

        Assert.Equal(FileContentRejection.ForbiddenSignature, result.Rejection);
    }

    [Fact]
    public void ValidationResultDoesNotContainInputMetadataOrContent()
    {
        var fileName = "private-name.pdf";
        var mediaType = "application/pdf";
        var content = Encoding.ASCII.GetBytes("%PDF-1.7");

        var result = FileContentValidator.Validate(fileName, mediaType, content);
        var resultText = result.ToString();

        Assert.DoesNotContain(fileName, resultText, StringComparison.Ordinal);
        Assert.DoesNotContain(mediaType, resultText, StringComparison.Ordinal);
        Assert.DoesNotContain("%PDF-1.7", resultText, StringComparison.Ordinal);
    }

    private static byte[] ValidContent(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".pdf" => Encoding.ASCII.GetBytes("%PDF-1.7"),
        ".png" => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00],
        ".jpg" or ".jpeg" => [0xFF, 0xD8, 0xFF, 0xD9],
        ".txt" => Encoding.UTF8.GetBytes("plain UTF-8 text"),
        _ => throw new InvalidOperationException("Test fixture extension is not supported.")
    };
}
