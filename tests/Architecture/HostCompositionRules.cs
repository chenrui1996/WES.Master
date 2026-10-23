using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Wes.Architecture.Tests;

internal static class HostCompositionRules
{
    internal static IReadOnlyList<string> Validate(CSharpCompilation compilation, IEnumerable<string> hostFiles)
    {
        var hosts = hostFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var violations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tree in compilation.SyntaxTrees.Where(tree => hosts.Contains(tree.FilePath)))
        {
            var semantic = compilation.GetSemanticModel(tree);
            foreach (var name in tree.GetRoot().DescendantNodes().OfType<NameSyntax>())
            {
                var symbol = semantic.GetSymbolInfo(name).Symbol;
                if (symbol is IAliasSymbol alias)
                {
                    symbol = alias.Target;
                }

                var space = symbol is INamespaceSymbol ns
                    ? ns.ToDisplayString()
                    : symbol?.ContainingNamespace?.ToDisplayString();
                if (space is not null && IsForbidden(space))
                {
                    var line = tree.GetLineSpan(name.Span).StartLinePosition.Line + 1;
                    violations.Add($"{tree.FilePath}:{line}: Host references forbidden implementation {space}.");
                }
            }
        }

        return violations.Order(StringComparer.Ordinal).ToArray();
    }

    private static bool IsForbidden(string space) =>
        IsPrefix(space, "Microsoft.EntityFrameworkCore")
        || IsPrefix(space, "MySql")
        || IsPrefix(space, "Npgsql")
        || IsPrefix(space, "Microsoft.Data.SqlClient")
        || IsPrefix(space, "System.Data")
        || IsPrefix(space, "NModbus")
        || IsPrefix(space, "HslCommunication")
        || IsPrefix(space, "Seven.Builder")
        || (space.StartsWith("Wes.", StringComparison.Ordinal)
            && space.Split('.').Any(segment => segment is "Domain" or "Infrastructure" or "Builder"));

    private static bool IsPrefix(string value, string prefix) =>
        value == prefix || value.StartsWith(prefix + ".", StringComparison.Ordinal);
}
