using System.Xml.Linq;

namespace Wes.Architecture.Tests;

internal sealed record ProjectEdge(string Source, string Target);

internal static class ProjectBoundaryRules
{
    internal static IReadOnlyList<string> Validate(IEnumerable<ProjectEdge> edges)
    {
        var violations = new List<string>();
        foreach (var edge in edges)
        {
            var source = Split(edge.Source);
            var target = Split(edge.Target);
            var sourceOwner = Owner(source);
            var targetOwner = Owner(target);
            var forbiddenBuilder = target.Any(part => part.Equals("Builder", StringComparison.OrdinalIgnoreCase))
                || Path.GetFileNameWithoutExtension(target[^1]).EndsWith(".Builder", StringComparison.OrdinalIgnoreCase);
            var moduleToHost = sourceOwner is not null
                && target.Length > 1 && target[1].Equals("Host", StringComparison.OrdinalIgnoreCase);
            var invertedLayer = source.Any(IsInnerLayer)
                && target.Any(part => part.Equals("Infrastructure", StringComparison.OrdinalIgnoreCase));
            var crossOwnerPrivate = sourceOwner is not null && targetOwner is not null
                && !sourceOwner.Equals(targetOwner, StringComparison.OrdinalIgnoreCase)
                && !IsContract(target);

            if (forbiddenBuilder || moduleToHost || invertedLayer || crossOwnerPrivate)
            {
                violations.Add($"Forbidden project dependency: {edge.Source} -> {edge.Target}");
            }
        }

        return violations;
    }

    internal static IReadOnlyList<ProjectEdge> ReadEdges(string root, IEnumerable<string> projects)
    {
        var rootPath = Path.GetFullPath(root);
        var edges = new List<ProjectEdge>();
        foreach (var project in projects)
        {
            var source = Path.GetFullPath(project);
            var relativeSource = RelativeProject(rootPath, source);
            var document = XDocument.Load(source);
            foreach (var reference in document.Descendants()
                .Where(element => element.Name.LocalName == "ProjectReference"))
            {
                var include = reference.Attribute("Include")?.Value;
                // Literal paths only: never silently omit an unevaluated MSBuild dependency.
                if (string.IsNullOrWhiteSpace(include) || include.IndexOfAny(['$', '@', '%', '*', '?', ';']) >= 0)
                {
                    throw new InvalidDataException($"Unsupported ProjectReference in {relativeSource}: {include}");
                }

                var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, include));
                edges.Add(new ProjectEdge(relativeSource, RelativeProject(rootPath, target)));
            }
        }

        return edges;
    }

    private static string RelativeProject(string root, string project)
    {
        if (!project.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(project)
            || !Path.GetExtension(project).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Project dependency is missing or outside the repository: {project}");
        }

        return Path.GetRelativePath(root, project);
    }

    private static string[] Split(string path) =>
        path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);

    private static bool IsInnerLayer(string part) =>
        part.Equals("Domain", StringComparison.OrdinalIgnoreCase)
        || part.Equals("Application", StringComparison.OrdinalIgnoreCase);

    private static string? Owner(string[] parts)
    {
        if (parts.Length < 4 || !parts[0].Equals("src", StringComparison.OrdinalIgnoreCase)
            || !parts[1].Equals("Modules", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return parts[2].Equals("Wcs", StringComparison.OrdinalIgnoreCase) && parts.Length >= 5
            ? $"Wcs.{parts[3]}"
            : parts[2];
    }

    private static bool IsContract(string[] parts)
    {
        var contractIndex = parts[2].Equals("Wcs", StringComparison.OrdinalIgnoreCase) ? 4 : 3;
        return parts.Length == contractIndex + 2
            && parts[contractIndex].Equals("Contracts", StringComparison.OrdinalIgnoreCase)
            && Path.GetFileNameWithoutExtension(parts[^1]).EndsWith(".Contracts", StringComparison.OrdinalIgnoreCase);
    }
}
