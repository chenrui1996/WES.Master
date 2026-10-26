using MySql.Data.MySqlClient;
using Xunit;

namespace Wes.Persistence.Tests;

/// <summary>Exercises target isolation using synthetic credentials, never the real source values.</summary>
public sealed class MySqlConnectionGuardTests
{
    /// <summary>Prevents an authorized credential source from selecting its original business database.</summary>
    [Fact]
    [Trait("CaseId", "MYSQL-GUARD-001")]
    public void AuthorizedSource_AlwaysSelectsTestDatabaseWithoutChangingSource()
    {
        const string source = "Server=127.0.0.1;Port=3307;Database=business;User ID=fixture;Password=synthetic;";

        var result = new MySqlConnectionStringBuilder(MySqlTestConnection.FromAuthorizedSource(source));

        Assert.Equal("wes_test", result.Database);
        Assert.Equal("127.0.0.1", result.Server);
        Assert.Equal(3307u, result.Port);
        Assert.Equal("fixture", result.UserID);
        Assert.Equal("synthetic", result.Password);
        Assert.False(result.AllowLoadLocalInfile);
        Assert.False(result.PersistSecurityInfo);
        Assert.Equal("business", new MySqlConnectionStringBuilder(source).Database);
    }

    /// <summary>Locates the one authorized sibling source in both normal and Git worktree layouts.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("CaseId", "MYSQL-GUARD-002")]
    public void AuthorizedSourcePath_ResolvesProjectSiblingFromRepositoryOrWorktreeRoot(bool worktree)
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"wes-path-{Guid.NewGuid():N}");
        var repositoryRoot = worktree
            ? Path.Combine(temporaryRoot, "WES.Master.worktrees", "worktree")
            : Path.Combine(temporaryRoot, "WES.Master");
        var expected = Path.Combine(temporaryRoot, "Seven.Master", "Seven.Net8", "Seven.WebApi", "appsettings.json");
        try
        {
            Directory.CreateDirectory(repositoryRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(expected)!);
            Directory.CreateDirectory(Path.Combine(repositoryRoot, "Seven.Master", "Seven.Net8", "Seven.WebApi"));

            Assert.Equal(expected, MySqlTestConnection.ResolveAuthorizedSourcePath(new DirectoryInfo(repositoryRoot)));
        }
        finally
        {
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    /// <summary>Rejects incomplete or malformed configuration without reflecting its contents.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("Not A Connection String=secret-marker;")]
    [InlineData("Server=;User ID=fixture;Password=secret-marker;")]
    [InlineData("Server=localhost;Password=secret-marker;")]
    [InlineData("Server=localhost;User ID=fixture;")]
    [InlineData("Server=localhost;User ID=fixture;Password=synthetic;Port=secret-marker;")]
    [InlineData("Server=localhost;User ID=fixture;Password=synthetic;Port=999999999999;")]
    [Trait("CaseId", "MYSQL-GUARD-NEG")]
    public void InvalidSource_IsRejectedWithoutSecretEcho(string source)
    {
        var error = Assert.Throws<InvalidOperationException>(() => MySqlTestConnection.FromAuthorizedSource(source));

        Assert.DoesNotContain("secret-marker", error.Message);
        Assert.Null(error.InnerException);
    }

    /// <summary>A second guard stops accidental retargeting before any socket is opened.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("mysql")]
    [InlineData("business")]
    [InlineData("WES_TEST")]
    [Trait("CaseId", "MYSQL-GUARD-NEG")]
    public void NonTestTarget_IsRejected(string database)
    {
        var connection = $"Server=localhost;User ID=fixture;Password=synthetic;Database={database};";

        Assert.Throws<InvalidOperationException>(() => MySqlTestConnection.ValidateTarget(connection));
    }
}
