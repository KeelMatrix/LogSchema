using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace KeelMatrix.LogSchema;

internal static class SourceProvenance
{
    internal static string NormalizeProjectRelativePath(string projectDirectory, string path)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory) || string.IsNullOrWhiteSpace(path))
        {
            throw new ProjectAnalysisException("A project document has no stable source identity.");
        }

        try
        {
            var fullProjectDirectory = Path.GetFullPath(projectDirectory);
            var portablePath = path.Replace('\\', '/');
            var fullPath = IsRootedPortablePath(portablePath)
                ? Path.GetFullPath(portablePath)
                : Path.GetFullPath(portablePath, fullProjectDirectory);
            var relativePath = Path.GetRelativePath(fullProjectDirectory, fullPath).Replace('\\', '/');
            var segments = SplitSegments(relativePath);
            var outsideSegments = 0;
            while (outsideSegments < segments.Count && segments[outsideSegments] == "..")
            {
                outsideSegments++;
            }

            if (segments.Skip(outsideSegments).Any(segment => segment is "." or ".."))
            {
                throw new ProjectAnalysisException("A project document has a non-canonical source identity.");
            }

            var remaining = segments.Skip(outsideSegments).ToArray();
            if (remaining.Length == 0)
            {
                throw new ProjectAnalysisException("A project document has no stable source identity.");
            }

            var prefix = outsideSegments == 0
                ? new[] { "project" }
                : ["external", "up-" + outsideSegments.ToString(System.Globalization.CultureInfo.InvariantCulture)];
            return JoinSegments(prefix.Concat(remaining), "project document");
        }
        catch (ProjectAnalysisException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or SecurityException)
        {
            throw new ProjectAnalysisException("A project document has no stable source identity.");
        }
    }

    internal static string NormalizeGeneratedPath(string? path, string? generatedText = null)
    {
        if (string.IsNullOrWhiteSpace(path) && generatedText is null)
        {
            throw new ProjectAnalysisException("A generated syntax tree has no stable generator or hint identity.");
        }

        try
        {
            var portablePath = path?.Replace('\\', '/') ?? string.Empty;
            var segments = string.IsNullOrWhiteSpace(portablePath)
                ? []
                : SplitSegments(portablePath, rejectColon: false);
            var generatedMarker = segments
                .Select((segment, index) => (segment, index))
                .Where(item => string.Equals(item.segment, "generated", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.index)
                .DefaultIfEmpty(-1)
                .Max();

            string[] stableSegments;
            if (generatedMarker >= 0)
            {
                stableSegments = segments.Skip(generatedMarker + 1).ToArray();
            }
            else
            {
                if (IsRootedPortablePath(portablePath))
                {
                    return generatedText is null
                        ? throw new ProjectAnalysisException("A generated syntax tree has no stable generator or hint identity.")
                        : CreateContentIdentity(generatedText);
                }

                stableSegments = segments.Count == 0
                    ? [CreateContentIdentity(generatedText!)["generated/".Length..]]
                    : ["logical", .. segments];
            }

            if (stableSegments.Length == 0 || stableSegments.Any(segment => segment is "." or ".."))
            {
                throw new ProjectAnalysisException("A generated syntax tree has no stable generator or hint identity.");
            }

            return JoinSegments(["generated", .. stableSegments], "generated syntax tree");
        }
        catch (ProjectAnalysisException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or SecurityException)
        {
            throw new ProjectAnalysisException("A generated syntax tree has no stable generator or hint identity.");
        }
    }

    private static List<string> SplitSegments(string path, bool rejectColon = true)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (segments.Count == 0 || segments.Any(segment => segment is "." or "" || rejectColon && segment.Contains(':')))
        {
            throw new ProjectAnalysisException("A source document has no safe logical identity.");
        }

        return segments;
    }

    private static string JoinSegments(IEnumerable<string> segments, string description)
    {
        var values = segments.ToArray();
        if (values.Length == 0 || values.Any(segment => segment is "." or ".." or "" || segment.Contains(':')))
        {
            throw new ProjectAnalysisException($"The {description} identity is not safe to persist.");
        }

        return string.Join('/', values);
    }

    private static bool IsRootedPortablePath(string path) =>
        path.StartsWith('/') ||
        path.StartsWith("//", StringComparison.Ordinal) ||
        path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':';

    private static string CreateContentIdentity(string generatedText)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(generatedText))).ToLowerInvariant();
        return "generated/content-" + hash;
    }
}

internal sealed class SourceProvenanceRegistry
{
    private readonly Dictionary<string, object> owners = new(StringComparer.Ordinal);

    internal bool TryRegister(string kind, string logicalPath, object physicalIdentity)
    {
        var key = kind + "\u001f" + logicalPath;
        if (owners.TryGetValue(key, out var owner))
        {
            return ReferenceEquals(owner, physicalIdentity);
        }

        owners.Add(key, physicalIdentity);
        return true;
    }
}
