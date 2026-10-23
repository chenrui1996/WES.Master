using Xunit;

namespace Wes.Architecture.Tests;

/// <summary>Checks composition and owner boundaries, with deliberate illegal graph edges.</summary>
public sealed class ModuleBoundaryTests
{
    /// <summary>Rejects private cross-owner references and inverted layer dependencies.</summary>
    [Theory]
    [InlineData(@"src\Modules\Wms\Wes.Wms.csproj", @"src\Modules\Orchestration\Infrastructure\Wes.Orchestration.Infrastructure.csproj")]
    [InlineData(@"src\Modules\Wms\Wes.Wms.csproj", @"src\Modules\Orchestration\Wes.Orchestration.DbContext.csproj")]
    [InlineData(@"src\Modules\Wms\Domain\Wes.Wms.Domain.csproj", @"src\Host\Wes.Host.csproj")]
    [InlineData(@"src\Modules\Wms\Application\Wes.Wms.Application.csproj", @"src\Host\Wes.Host.csproj")]
    [InlineData(@"src\Modules\Wms\Domain\Wes.Wms.Domain.csproj", @"src\Modules\Wms\Infrastructure\Wes.Wms.Infrastructure.csproj")]
    [InlineData(@"src\Modules\Wms\Application\Wes.Wms.Application.csproj", @"src\Modules\Wms\Infrastructure\Wes.Wms.Infrastructure.csproj")]
    [InlineData(@"src\Modules\Wcs\Stacker\Wes.Wcs.Stacker.csproj", @"src\Modules\Wcs\FourWay\Wes.Wcs.FourWay.csproj")]
    [InlineData(@"src\Modules\Wms\Wes.Wms.csproj", @"src\Modules\Orchestration\Wes.Orchestration.ContractsFake.csproj")]
    [Trait("CaseId", "ARCH-TC-001-NEG")]
    public void Dependency_ReferencesPrivateOrInvertedBoundary_IsRejected(string source, string target)
    {
        // Arrange
        var edge = new ProjectEdge(source, target);

        // Act
        var violations = ProjectBoundaryRules.Validate([edge]);

        // Assert
        Assert.Single(violations);
    }

    /// <summary>Rejects the removed Builder capability at the composition boundary.</summary>
    [Fact]
    [Trait("CaseId", "ARCH-BUILDER-NEG")]
    public void Host_ReferencesBuilder_IsRejected()
    {
        // Arrange
        var edge = new ProjectEdge(@"src\Host\Wes.Host.csproj", @"src\Builder\Wes.Builder.csproj");

        // Act
        var violations = ProjectBoundaryRules.Validate([edge]);

        // Assert
        Assert.Single(violations);
    }

    /// <summary>Allows composition, public owner contracts and inward module dependencies.</summary>
    [Theory]
    [InlineData(@"src\Host\Wes.Host.csproj", @"src\Modules\Wms\Wes.Wms.csproj")]
    [InlineData(@"src\Modules\Wms\Wes.Wms.csproj", @"src\Modules\Orchestration\Contracts\Wes.Orchestration.Contracts.csproj")]
    [InlineData(@"src\Modules\Wcs\Stacker\Wes.Wcs.Stacker.csproj", @"src\Modules\Wcs\FourWay\Contracts\Wes.Wcs.FourWay.Contracts.csproj")]
    [InlineData(@"src\Modules\Wms\Application\Wes.Wms.Application.csproj", @"src\Modules\Wms\Domain\Wes.Wms.Domain.csproj")]
    [Trait("CaseId", "ARCH-TC-001")]
    public void Dependency_ReferencesCompositionOrPublicContract_IsAllowed(string source, string target)
    {
        // Arrange
        var edge = new ProjectEdge(source, target);

        // Act
        var violations = ProjectBoundaryRules.Validate([edge]);

        // Assert
        Assert.Empty(violations);
    }

    /// <summary>Checks the current production graph without adding fixture projects to it.</summary>
    [Fact]
    [Trait("CaseId", "ARCH-TC-001")]
    public void ProductionProjects_RespectOwnerDependencyRules()
    {
        // Arrange
        var root = FindRepositoryRoot();
        var projects = Directory.GetFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories);
        Assert.NotEmpty(projects);

        // Act
        var edges = ProjectBoundaryRules.ReadEdges(root, projects);
        var violations = ProjectBoundaryRules.Validate(edges);

        // Assert
        Assert.Empty(violations);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "WES.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Cannot locate WES.slnx for architecture tests.");
    }
}
