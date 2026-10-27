using Wes.Platform.Domain;
using Xunit;

namespace Wes.Modules.Tests.Platform;

public sealed class DictionaryCodeTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("01")]
    [InlineData("1Ready")]
    [InlineData("Ready1")]
    [InlineData("Ready._-0")]
    public void DictionaryCodePreservesExactStableString(string code)
    {
        var entry = new DictionaryEntry(Guid.NewGuid(), "task", code, "task.ready");
        Assert.Equal(code, entry.Code);
        Assert.Equal("task", entry.Category);
        Assert.Equal("task.ready", entry.LanguageKey);
    }

    [Fact]
    public void DictionaryCodePreservesExistingLengthBoundary()
    {
        Assert.Equal(128, new DictionaryEntry(Guid.NewGuid(), "task", "0" + new string('a', 127), "task.ready").Code.Length);
        Assert.Throws<ArgumentException>(() => new DictionaryEntry(Guid.NewGuid(), "task", "0" + new string('a', 128), "task.ready"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" 0")]
    [InlineData("0 ")]
    [InlineData("0\n")]
    [InlineData("0\u0000")]
    [InlineData("-0")]
    [InlineData(".0")]
    [InlineData("_0")]
    [InlineData("０")]
    [InlineData("<script>")]
    public void DictionaryCodeRejectsInvalidText(string? code) =>
        Assert.Throws<ArgumentException>(() => new DictionaryEntry(Guid.NewGuid(), "task", code!, "task.ready"));

    [Fact]
    public void NumericDictionaryCodesDoNotRelaxOtherTechnicalIdentifiers()
    {
        Assert.Throws<ArgumentException>(() => new DictionaryEntry(Guid.NewGuid(), "0", "Ready", "task.ready"));
        Assert.Throws<ArgumentException>(() => new DictionaryEntry(Guid.NewGuid(), "task", "Ready", "0"));
        Assert.Throws<ArgumentException>(() => new DirectoryDepartment(Guid.NewGuid(), "0", "Department"));
        Assert.Throws<ArgumentException>(() => new AccessRole(Guid.NewGuid(), "0", []));
    }
}
