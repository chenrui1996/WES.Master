using Xunit;

namespace Wes.Persistence.Tests;

/// <summary>Verifies source configuration parsing and secret-safe diagnostics with synthetic files.</summary>
public sealed class MySqlSourceTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("wes-mysql-source-");

    /// <summary>Supports appsettings comments and trailing commas without changing the source file.</summary>
    [Fact]
    [Trait("CaseId", "MYSQL-GUARD-001")]
    public void Source_WithCommentsAndTrailingComma_ReadsDatabaseConnection()
    {
        const string content = """
            { // synthetic source
              "Database": {
                "ConnectionString": "Server=localhost;Database=business;User ID=fixture;Password=synthetic;",
              },
            }
            """;
        var path = Write(content);

        var source = MySqlTestConnection.ReadSource(path);

        Assert.Contains("Database=business", source);
        Assert.Equal(content, File.ReadAllText(path));
    }

    /// <summary>Invalid JSON and absent sections fail explicitly without revealing payloads.</summary>
    [Theory]
    [InlineData("{ secret-marker")]
    [InlineData("{}")]
    [InlineData("""{"Database":{"ConnectionString":null}}""")]
    [InlineData("""{"Database":{"ConnectionString":123}}""")]
    [InlineData("""{"Database":[]}""")]
    [InlineData("[]")]
    [Trait("CaseId", "MYSQL-GUARD-NEG")]
    public void InvalidSourceFile_FailsWithoutConfigurationEcho(string content)
    {
        var path = Write(content);

        var error = Assert.Throws<InvalidOperationException>(() => MySqlTestConnection.ReadSource(path));

        Assert.DoesNotContain("secret-marker", error.Message);
        Assert.Null(error.InnerException);
    }

    /// <summary>Missing configuration is an explicit failure rather than a default or skipped test.</summary>
    [Fact]
    [Trait("CaseId", "MYSQL-GUARD-NEG")]
    public void MissingSourceFile_FailsExplicitly()
    {
        var path = Path.Combine(_directory.FullName, "missing.json");

        var error = Assert.Throws<InvalidOperationException>(() => MySqlTestConnection.ReadSource(path));

        Assert.Null(error.InnerException);
    }

    /// <summary>Deletes only this test's owned synthetic configuration directory.</summary>
    public void Dispose() => _directory.Delete(recursive: true);

    private string Write(string content)
    {
        var path = Path.Combine(_directory.FullName, "synthetic.json");
        File.WriteAllText(path, content);
        return path;
    }
}
