using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KeelMatrix.LogSchema;

internal sealed record ManifestDocument(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("projects")] IReadOnlyList<ProjectIdentity> Projects,
    [property: JsonPropertyName("events")] IReadOnlyList<EventContract> Events,
    [property: JsonPropertyName("unsupported")] IReadOnlyList<UnsupportedDeclaration> Unsupported,
    [property: JsonPropertyName("analysisIssues")] IReadOnlyList<AnalysisIssue> AnalysisIssues,
    [property: JsonPropertyName("compilationDiagnosticKinds")] IReadOnlyList<string> CompilationDiagnosticKinds,
    [property: JsonPropertyName("workspaceDiagnosticKinds")] IReadOnlyList<string> WorkspaceDiagnosticKinds)
{
    internal static ManifestDocument Empty => new(
        1,
        Array.Empty<ProjectIdentity>(),
        Array.Empty<EventContract>(),
        Array.Empty<UnsupportedDeclaration>(),
        Array.Empty<AnalysisIssue>(),
        Array.Empty<string>(),
        Array.Empty<string>());

    internal ManifestDocument Canonicalize() => this with
    {
        Projects = Projects.OrderBy(project => project.Key, StringComparer.Ordinal).ToArray(),
        Events = Events.OrderBy(@event => @event.ProjectKey, StringComparer.Ordinal).ThenBy(@event => @event.Identity, StringComparer.Ordinal).ToArray(),
        Unsupported = Unsupported
            .OrderBy(item => item.ProjectKey, StringComparer.Ordinal)
            .ThenBy(item => item.Source.File, StringComparer.Ordinal)
            .ThenBy(item => item.Source.Line)
            .ThenBy(item => item.DeclarationKey, StringComparer.Ordinal)
            .ToArray(),
        AnalysisIssues = AnalysisIssues
            .OrderBy(issue => issue.ProjectKey, StringComparer.Ordinal)
            .ThenBy(issue => issue.Code, StringComparer.Ordinal)
            .ThenBy(issue => issue.DeclarationKey, StringComparer.Ordinal)
            .ToArray(),
        CompilationDiagnosticKinds = CompilationDiagnosticKinds.Order(StringComparer.Ordinal).ToArray(),
        WorkspaceDiagnosticKinds = WorkspaceDiagnosticKinds.Order(StringComparer.Ordinal).ToArray()
    };
}

internal sealed record ProjectIdentity(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("assembly")] string Assembly,
    [property: JsonPropertyName("targetFramework")] string TargetFramework);

internal sealed record EventContract(
    [property: JsonPropertyName("projectKey")] string ProjectKey,
    [property: JsonPropertyName("identity")] string Identity,
    [property: JsonPropertyName("containingType")] string ContainingType,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("genericArity")] int GenericArity,
    [property: JsonPropertyName("parameterRefKinds")] IReadOnlyList<string> ParameterRefKinds,
    [property: JsonPropertyName("eventId")] int EventId,
    [property: JsonPropertyName("eventName")] string EventName,
    [property: JsonPropertyName("level")] string Level,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("placeholders")] IReadOnlyList<Placeholder> Placeholders,
    [property: JsonPropertyName("parameterForms")] IReadOnlyList<string> ParameterForms,
    [property: JsonPropertyName("source")] SourceLocation Source);

internal sealed record Placeholder(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("token")] string Token);

internal sealed record UnsupportedDeclaration(
    [property: JsonPropertyName("projectKey")] string ProjectKey,
    [property: JsonPropertyName("source")] SourceLocation Source,
    [property: JsonPropertyName("declaration")] string Declaration,
    [property: JsonPropertyName("declarationKey")] string DeclarationKey,
    [property: JsonPropertyName("reason")] string Reason);

internal sealed record AnalysisIssue(
    [property: JsonPropertyName("projectKey")] string ProjectKey,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("declarationKey")] string DeclarationKey,
    [property: JsonPropertyName("sources")] IReadOnlyList<SourceLocation> Sources);

internal sealed record SourceLocation(
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("kind")] string Kind);

internal static class ManifestJson
{
    internal const int MaxBytes = 4 * 1024 * 1024;
    internal const int MaxItems = 100_000;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        MaxDepth = 32,
        PropertyNameCaseInsensitive = false
    };

    internal static async Task WriteAsync(ManifestDocument manifest, string path, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(manifest.Canonicalize(), Options);
        json = json.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n') + "\n";
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(path, json, new UTF8Encoding(false), cancellationToken);
    }

    internal static async Task<ManifestDocument> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new ManifestReadException("The manifest file was not found.");
        }

        var info = new FileInfo(path);
        if (info.Length > MaxBytes)
        {
            throw new ManifestReadException("The manifest exceeds the 4 MiB safety limit.");
        }

        byte[] bytes;
        await using (var stream = File.OpenRead(path))
        {
            bytes = new byte[(int)info.Length];
            var read = await stream.ReadAsync(bytes, cancellationToken);
            if (read != bytes.Length)
            {
                throw new ManifestReadException("The manifest could not be read completely.");
            }
        }

        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            if (!document.RootElement.TryGetProperty("schemaVersion", out var schemaVersion) || schemaVersion.ValueKind != JsonValueKind.Number || schemaVersion.GetInt32() != 1)
            {
                throw new ManifestReadException("The manifest schemaVersion is unsupported; only schemaVersion 1 is accepted.");
            }

            var manifest = JsonSerializer.Deserialize<ManifestDocument>(bytes, Options)
                ?? throw new ManifestReadException("The manifest is empty.");
            Validate(manifest);
            return manifest.Canonicalize();
        }
        catch (ManifestReadException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or OverflowException or InvalidOperationException)
        {
            throw new ManifestReadException($"The manifest is malformed: {exception.Message}");
        }
    }

    private static void Validate(ManifestDocument manifest)
    {
        if (manifest.SchemaVersion != 1 || manifest.Projects is null || manifest.Events is null || manifest.Unsupported is null || manifest.AnalysisIssues is null)
        {
            throw new ManifestReadException("The manifest is missing required schema v1 fields.");
        }

        if (manifest.Projects.Count > MaxItems || manifest.Events.Count > MaxItems || manifest.Unsupported.Count > MaxItems || manifest.AnalysisIssues.Count > MaxItems)
        {
            throw new ManifestReadException("The manifest contains more records than the safety limit permits.");
        }

        if (manifest.Projects.GroupBy(project => project.Key, StringComparer.Ordinal).Any(group => group.Count() > 1) ||
            manifest.Events.GroupBy(@event => @event.ProjectKey + "\u001f" + @event.Identity, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            throw new ManifestReadException("The manifest contains duplicate or ambiguous canonical identities.");
        }

        foreach (var project in manifest.Projects)
        {
            if (string.IsNullOrWhiteSpace(project.Key) || string.IsNullOrWhiteSpace(project.Assembly))
            {
                throw new ManifestReadException("A manifest project identity is incomplete.");
            }
        }

        foreach (var @event in manifest.Events)
        {
            if (@event.Placeholders is null || @event.Placeholders.Count > MaxItems || string.IsNullOrWhiteSpace(@event.Identity))
            {
                throw new ManifestReadException("A manifest event is incomplete or too large.");
            }
            ValidateSource(@event.Source);
        }

        foreach (var item in manifest.Unsupported)
        {
            ValidateSource(item.Source);
        }

        foreach (var issue in manifest.AnalysisIssues)
        {
            if (issue.Sources is null || issue.Sources.Count > MaxItems)
            {
                throw new ManifestReadException("A manifest analysis issue is too large.");
            }
            foreach (var source in issue.Sources)
            {
                ValidateSource(source);
            }
        }
    }

    private static void ValidateSource(SourceLocation source)
    {
        if (source is null || string.IsNullOrWhiteSpace(source.File) || Path.IsPathRooted(source.File) || source.File.Contains('\\', StringComparison.Ordinal) || source.File.Contains(':', StringComparison.Ordinal))
        {
            throw new ManifestReadException("A manifest source location contains an absolute or non-canonical path.");
        }
    }
}

internal sealed class ManifestReadException(string message) : Exception(message);
