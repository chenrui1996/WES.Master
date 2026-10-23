using Xunit;

namespace Wes.Architecture.Tests;

/// <summary>
/// Exercises project-file parsing against isolated files, including fail-closed inputs.
/// Every fixture owns and removes only its unique temporary directory.
/// </summary>
public sealed class ProjectGraphTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("wes-project-graph-");

    /// <summary>Proves that relative XML references reach the same owner-boundary validator.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("http://schemas.microsoft.com/developer/msbuild/2003")]
    [Trait("CaseId", "ARCH-GRAPH-001")]
    public void ReadEdges_RelativeProjectReference_PreservesIllegalDependency(string xmlNamespace)
    {
        var source = WriteProject(@"src\Modules\Wms\Wes.Wms.csproj",
            $"""
            <Project xmlns="{xmlNamespace}">
              <ItemGroup>
                <ProjectReference Include="..\Orchestration\Wes.Orchestration.csproj" />
              </ItemGroup>
            </Project>
            """);
        WriteProject(@"src\Modules\Orchestration\Wes.Orchestration.csproj", "<Project />");

        var edges = ProjectBoundaryRules.ReadEdges(_directory.FullName, [source]);

        var edge = Assert.Single(edges);
        Assert.Equal(@"src\Modules\Wms\Wes.Wms.csproj", edge.Source);
        Assert.Equal(@"src\Modules\Orchestration\Wes.Orchestration.csproj", edge.Target);
        Assert.Single(ProjectBoundaryRules.Validate(edges));
    }

    /// <summary>Leaf projects legitimately have no edges.</summary>
    [Fact]
    [Trait("CaseId", "ARCH-GRAPH-001")]
    public void ReadEdges_LeafProject_ReturnsEmpty()
    {
        var source = WriteProject(@"src\Host\Wes.Host.csproj", "<Project />");

        Assert.Empty(ProjectBoundaryRules.ReadEdges(_directory.FullName, [source]));
    }

    /// <summary>Unsupported or missing references must not disappear from the gate.</summary>
    [Theory]
    [InlineData("")]
    [InlineData(@"$(ModuleRoot)\Wes.Wms.csproj")]
    [InlineData(@"..\Missing\Wes.Missing.csproj")]
    [InlineData(@"..\..\..\outside.csproj")]
    [Trait("CaseId", "ARCH-GRAPH-NEG")]
    public void ReadEdges_UnresolvableReference_FailsExplicitly(string include)
    {
        var source = WriteProject(@"src\Host\Wes.Host.csproj",
            $"""<Project><ItemGroup><ProjectReference Include="{include}" /></ItemGroup></Project>""");

        Assert.Throws<InvalidDataException>(() =>
            ProjectBoundaryRules.ReadEdges(_directory.FullName, [source]));
    }

    /// <summary>Removes only the unique temporary fixture created by this test instance.</summary>
    public void Dispose()
    {
        _directory.Delete(recursive: true);
    }

    private string WriteProject(string relativePath, string xml)
    {
        var path = Path.Combine(_directory.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, xml);
        return path;
    }
}
