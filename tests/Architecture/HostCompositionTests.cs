using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Wes.Architecture.Tests;

/// <summary>Compiles actual and deliberately illegal Host sources, then checks resolved symbols rather than text.</summary>
public sealed class HostCompositionTests
{
    /// <summary>Fully-qualified and aliased references must not bypass the composition boundary.</summary>
    [Theory]
    [InlineData("namespace Microsoft.EntityFrameworkCore { public class DbContext { public void SaveChanges() {} } }",
        "using DB = Microsoft.EntityFrameworkCore.DbContext; class Endpoint { void Handle(DB db) => db.SaveChanges(); }")]
    [InlineData("namespace MySql.Data.MySqlClient { public class MySqlCommand {} }",
        "class Endpoint { object Handle() => new MySql.Data.MySqlClient.MySqlCommand(); }")]
    [InlineData("namespace Wes.Wms.Domain { public class Stock { public int Quantity {get;set;} } }",
        "class Endpoint { void Handle(Wes.Wms.Domain.Stock stock) { stock.Quantity -= 1; } }")]
    [InlineData("namespace Wes.Platform.Infrastructure { public class Persistence {} }",
        "class Endpoint { object Handle() => new Wes.Platform.Infrastructure.Persistence(); }")]
    [InlineData("namespace NModbus { public class Master {} }",
        "class Endpoint { object Handle() => new NModbus.Master(); }")]
    [InlineData("namespace Seven.Builder { public class Generator {} }",
        "class Endpoint { object Handle() => new Seven.Builder.Generator(); }")]
    [Trait("CaseId", "ARCH-HOST-NEG")]
    public void Host_ReferencesForbiddenImplementation_IsRejected(string support, string host)
    {
        var compilation = Compile(
            CSharpSyntaxTree.ParseText(support, path: "support.cs"),
            CSharpSyntaxTree.ParseText(host, path: "Host.cs"));
        AssertNoCompileErrors(compilation);

        Assert.NotEmpty(HostCompositionRules.Validate(compilation, ["Host.cs"]));
    }

    /// <summary>Benign composition is allowed even when comments and literals mention forbidden terms.</summary>
    [Fact]
    [Trait("CaseId", "ARCH-HOST-NEG")]
    public void Host_ReferencesPublicCompositionContract_IsAllowed()
    {
        var compilation = Compile(
            CSharpSyntaxTree.ParseText("namespace Wes.Platform { public class PlatformModule {} }", path: "support.cs"),
            CSharpSyntaxTree.ParseText("""
                // Never use MySql.Data or Seven.Builder here.
                class Endpoint { string Hint = "Microsoft.EntityFrameworkCore"; object Compose() => new Wes.Platform.PlatformModule(); }
                """, path: "Host.cs"));
        AssertNoCompileErrors(compilation);

        Assert.Empty(HostCompositionRules.Validate(compilation, ["Host.cs"]));
    }

    /// <summary>All actual Host sources must compile and avoid the forbidden implementation surface.</summary>
    [Fact]
    [Trait("CaseId", "ARCH-HOST-NEG")]
    public void ActualHost_ContainsOnlyCompositionAndHttpAdapters()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "WES.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var hostRoot = Path.Combine(root.FullName, "src", "Host");
        var files = Directory.GetFiles(hostRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains("\\obj\\") && !path.Contains("\\bin\\")).ToArray();
        Assert.NotEmpty(files);
        var trees = files.Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path)).ToList();
        trees.Add(CSharpSyntaxTree.ParseText("""
            global using System;
            global using System.Collections.Generic;
            global using System.IO;
            global using System.Linq;
            global using System.Threading;
            global using System.Threading.Tasks;
            global using Microsoft.AspNetCore.Builder;
            global using Microsoft.AspNetCore.Hosting;
            global using Microsoft.AspNetCore.Http;
            global using Microsoft.Extensions.Configuration;
            global using Microsoft.Extensions.DependencyInjection;
            global using Microsoft.Extensions.Hosting;
            global using Microsoft.Extensions.Logging;
            """, path: "ImplicitUsings.cs"));
        var compilation = Compile([.. trees], OutputKind.ConsoleApplication);
        AssertNoCompileErrors(compilation);

        Assert.Empty(HostCompositionRules.Validate(compilation, files));
    }

    private static CSharpCompilation Compile(SyntaxTree tree, SyntaxTree host) => Compile([tree, host]);

    private static CSharpCompilation Compile(SyntaxTree[] trees, OutputKind kind = OutputKind.DynamicallyLinkedLibrary)
    {
        var platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var binaries = Directory.GetFiles(AppContext.BaseDirectory, "*.dll");
        var references = platform.Concat(binaries).GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Key is not "Wes.Host.dll" and not "Wes.Architecture.Tests.dll")
            .Select(group => MetadataReference.CreateFromFile(group.Last())).ToArray();
        return CSharpCompilation.Create("HostFixture", trees, references, new CSharpCompilationOptions(kind));
    }

    private static void AssertNoCompileErrors(CSharpCompilation compilation) =>
        Assert.Empty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
}
