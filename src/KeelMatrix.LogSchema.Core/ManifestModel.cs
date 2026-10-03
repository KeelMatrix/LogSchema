using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace KeelMatrix.LogSchema;

internal sealed record ManifestDocument(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("projects")] IReadOnlyList<ProjectIdentity> Projects,
    [property: JsonPropertyName("events")] IReadOnlyList<EventContract> Events,
    [property: JsonPropertyName("unsupported")] IReadOnlyList<UnsupportedDeclaration> Unsupported,
    [property: JsonPropertyName("analysisIssues")] IReadOnlyList<AnalysisIssue> AnalysisIssues,
    [property: JsonPropertyName("compilationDiagnosticKinds")] IReadOnlyList<string> CompilationDiagnosticKinds,
    [property: JsonPropertyName("workspaceDiagnosticKinds")] IReadOnlyList<string> WorkspaceDiagnosticKinds,
    [property: JsonPropertyName("integrity")] string? Integrity = null)
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
    [property: JsonPropertyName("source")] SourceLocation Source,
    [property: JsonPropertyName("parameters")] IReadOnlyList<ParameterContract> Parameters,
    [property: JsonPropertyName("structuredState")] IReadOnlyList<StructuredStateProperty> StructuredState,
    [property: JsonPropertyName("loggerParameter")] string LoggerParameter,
    [property: JsonPropertyName("exceptionParameter")] string? ExceptionParameter,
    [property: JsonPropertyName("levelSource")] string LevelSource,
    [property: JsonPropertyName("levelParameter")] string? LevelParameter);

internal sealed record Placeholder(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("token")] string Token);

internal sealed record ParameterContract(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("refKind")] string RefKind,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("codeName"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CodeName = null);

internal sealed record StructuredStateProperty(
    [property: JsonPropertyName("parameterName")] string ParameterName,
    [property: JsonPropertyName("emittedName")] string EmittedName);

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

    private static readonly HashSet<string> ParameterRefKinds = new(StringComparer.Ordinal)
    {
        "None",
        "Ref",
        "Out",
        "In",
        "RefReadOnly",
        "RefReadOnlyParameter"
    };

    private static readonly HashSet<string> ParameterForms = new(StringComparer.Ordinal)
    {
        "Exception",
        "ILogger",
        "LogLevel",
        "None"
    };

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        MaxDepth = 32,
        PropertyNameCaseInsensitive = false
    };

    private static readonly JsonSerializerOptions IntegrityOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        MaxDepth = 32,
        PropertyNameCaseInsensitive = false
    };

    internal static async Task WriteAsync(ManifestDocument manifest, string path, CancellationToken cancellationToken)
    {
        string? temporaryPath = null;
        try
        {
            Validate(manifest);
            var canonical = manifest.Canonicalize() with { Integrity = null };
            Validate(canonical);
            var persisted = canonical with { Integrity = ComputeIntegrity(canonical) };
            var json = JsonSerializer.Serialize(persisted, Options)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n') + "\n";
            var bytes = new UTF8Encoding(false).GetBytes(json);
            if (bytes.Length > MaxBytes)
            {
                throw new ManifestWriteException("The manifest exceeds the 4 MiB safety limit.");
            }

            _ = DeserializeAndValidate(bytes);

            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath);
            if (directory is null)
            {
                throw new ManifestWriteException("The manifest output destination is invalid.");
            }

            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(directory, "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
            temporaryPath = null;
        }
        catch (ManifestWriteException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ManifestValidationException)
        {
            throw new ManifestWriteException("The manifest could not be validated for writing.");
        }
        catch (JsonException)
        {
            throw new ManifestWriteException("The manifest could not be validated for writing.");
        }
        catch (OverflowException)
        {
            throw new ManifestWriteException("The manifest could not be validated for writing.");
        }
        catch (InvalidOperationException)
        {
            throw new ManifestWriteException("The manifest could not be validated for writing.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SecurityException)
        {
            throw new ManifestWriteException("The manifest could not be written.");
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    internal static async Task<ManifestDocument> ReadAsync(string path, CancellationToken cancellationToken)
    {
        string fullPath;
        FileInfo info;
        try
        {
            fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
            {
                throw new ManifestReadException("The manifest file was not found.");
            }

            info = new FileInfo(fullPath);
        }
        catch (ManifestReadException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SecurityException)
        {
            throw new ManifestReadException("The manifest could not be read.");
        }

        if (info.Length > MaxBytes)
        {
            throw new ManifestReadException("The manifest exceeds the 4 MiB safety limit.");
        }

        byte[] bytes;
        try
        {
            await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > MaxBytes)
            {
                throw new ManifestReadException("The manifest exceeds the 4 MiB safety limit.");
            }

            bytes = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(bytes, cancellationToken);
        }
        catch (ManifestReadException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (EndOfStreamException)
        {
            throw new ManifestReadException("The manifest could not be read completely.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SecurityException)
        {
            throw new ManifestReadException("The manifest could not be read.");
        }

        try
        {
            return DeserializeAndValidate(bytes).Canonicalize();
        }
        catch (ManifestValidationException exception)
        {
            throw new ManifestReadException(exception.Message);
        }
        catch (JsonException)
        {
            throw new ManifestReadException("The manifest is malformed.");
        }
        catch (OverflowException)
        {
            throw new ManifestReadException("The manifest is malformed.");
        }
        catch (InvalidOperationException)
        {
            throw new ManifestReadException("The manifest is malformed.");
        }
    }

    private static ManifestDocument DeserializeAndValidate(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        ValidateJsonShape(document.RootElement);
        var manifest = JsonSerializer.Deserialize<ManifestDocument>(bytes, Options)
            ?? throw new ManifestValidationException("The manifest is empty.");
        Validate(manifest);
        ValidateIntegrity(manifest);
        return manifest;
    }

    private static string ComputeIntegrity(ManifestDocument manifest)
    {
        var unsigned = manifest with { Integrity = null };
        var json = JsonSerializer.Serialize(unsigned.Canonicalize(), IntegrityOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    }

    private static void ValidateIntegrity(ManifestDocument manifest)
    {
        if (manifest.Integrity is null)
        {
            throw new ManifestValidationException("The manifest integrity value is required; unsigned legacy v1 manifests cannot be compared.");
        }

        if (manifest.Integrity.Length != 64 || !manifest.Integrity.All(Uri.IsHexDigit) ||
            !string.Equals(manifest.Integrity, ComputeIntegrity(manifest), StringComparison.Ordinal))
        {
            throw new ManifestValidationException("The manifest integrity value does not match its canonical contents.");
        }
    }

    private static void ValidateJsonShape(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ManifestValidationException("The manifest root must be an object.");
        }

        RejectUnknownProperties(root, "root", "schemaVersion", "projects", "events", "unsupported", "analysisIssues", "compilationDiagnosticKinds", "workspaceDiagnosticKinds", "integrity");

        _ = RequiredInteger(root, "schemaVersion");
        foreach (var name in new[] { "projects", "events", "unsupported", "analysisIssues", "compilationDiagnosticKinds", "workspaceDiagnosticKinds" })
        {
            _ = RequiredArray(root, name);
        }
        if (!root.TryGetProperty("integrity", out var integrity))
        {
            throw new ManifestValidationException("The manifest integrity value is required; unsigned legacy v1 manifests cannot be compared.");
        }

        if (integrity.ValueKind != JsonValueKind.String)
        {
            throw new ManifestValidationException("The manifest field 'integrity' must be a string.");
        }

        foreach (var project in RequiredArray(root, "projects").EnumerateArray())
        {
            RequireObject(project, "projects");
            RejectUnknownProperties(project, "project", "key", "name", "assembly", "targetFramework");
            RequireString(project, "key");
            RequireString(project, "name");
            RequireString(project, "assembly");
            RequireString(project, "targetFramework");
        }

        foreach (var @event in RequiredArray(root, "events").EnumerateArray())
        {
            RequireObject(@event, "events");
            RejectUnknownProperties(@event, "event", "projectKey", "identity", "containingType", "method", "genericArity", "parameterRefKinds", "eventId", "eventName", "level", "message", "placeholders", "parameterForms", "source", "parameters", "structuredState", "loggerParameter", "exceptionParameter", "levelSource", "levelParameter");
            RequireString(@event, "projectKey");
            RequireString(@event, "identity");
            RequireString(@event, "containingType");
            RequireString(@event, "method");
            _ = RequiredInteger(@event, "genericArity");
            RequireStringArray(@event, "parameterRefKinds");
            _ = RequiredInteger(@event, "eventId");
            RequireString(@event, "eventName");
            RequireString(@event, "level");
            RequireString(@event, "message");
            foreach (var placeholder in RequiredArray(@event, "placeholders").EnumerateArray())
            {
                RequireObject(placeholder, "placeholders");
                RejectUnknownProperties(placeholder, "placeholder", "name", "token");
                RequireString(placeholder, "name");
                RequireString(placeholder, "token");
            }
            RequireStringArray(@event, "parameterForms");
            foreach (var parameter in RequiredArray(@event, "parameters").EnumerateArray())
            {
                RequireObject(parameter, "parameters");
                RejectUnknownProperties(parameter, "parameter", "name", "type", "refKind", "role", "codeName");
                RequireString(parameter, "name");
                RequireString(parameter, "type");
                RequireString(parameter, "refKind");
                RequireString(parameter, "role");
                if (parameter.TryGetProperty("codeName", out var codeName) && codeName.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                {
                    throw new ManifestValidationException("The manifest parameter field 'codeName' must be a string or null.");
                }
            }
            foreach (var property in RequiredArray(@event, "structuredState").EnumerateArray())
            {
                RequireObject(property, "structuredState");
                RejectUnknownProperties(property, "structuredState", "parameterName", "emittedName");
                RequireString(property, "parameterName");
                RequireString(property, "emittedName");
            }
            RequireNullableString(@event, "loggerParameter");
            RequireNullableString(@event, "exceptionParameter");
            RequireString(@event, "levelSource");
            RequireNullableString(@event, "levelParameter");
            ValidateJsonSource(@event, "source");
        }

        foreach (var item in RequiredArray(root, "unsupported").EnumerateArray())
        {
            RequireObject(item, "unsupported");
            RejectUnknownProperties(item, "unsupported", "projectKey", "source", "declaration", "declarationKey", "reason");
            RequireString(item, "projectKey");
            ValidateJsonSource(item, "source");
            RequireString(item, "declaration");
            RequireString(item, "declarationKey");
            RequireString(item, "reason");
        }

        foreach (var issue in RequiredArray(root, "analysisIssues").EnumerateArray())
        {
            RequireObject(issue, "analysisIssues");
            RejectUnknownProperties(issue, "analysis issue", "projectKey", "code", "severity", "message", "declarationKey", "sources");
            RequireString(issue, "projectKey");
            RequireString(issue, "code");
            RequireString(issue, "severity");
            RequireString(issue, "message");
            RequireString(issue, "declarationKey");
            foreach (var source in RequiredArray(issue, "sources").EnumerateArray())
            {
                if (source.ValueKind != JsonValueKind.Object)
                {
                    throw new ManifestValidationException("A manifest source record must be an object.");
                }
                RejectUnknownProperties(source, "source", "file", "line", "kind");
                RequireString(source, "file");
                _ = RequiredInteger(source, "line");
                RequireString(source, "kind");
            }
        }

        RequireStringArray(root, "compilationDiagnosticKinds");
        RequireStringArray(root, "workspaceDiagnosticKinds");
    }

    private static void RejectUnknownProperties(JsonElement value, string context, params string[] knownNames)
    {
        var known = knownNames.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw new ManifestValidationException($"The manifest contains duplicate field '{property.Name}' in {context}.");
            }

            if (!known.Contains(property.Name))
            {
                throw new ManifestValidationException($"The manifest contains unknown field '{property.Name}' in {context}.");
            }
        }
    }

    private static JsonElement RequiredArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            throw new ManifestValidationException($"The manifest field '{name}' is required and must be an array.");
        }

        return value;
    }

    private static int RequiredInteger(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || value.GetRawText().IndexOfAny(['.', 'e', 'E']) >= 0 || !value.TryGetInt32(out var integer))
        {
            throw new ManifestValidationException($"The manifest field '{name}' is required and must be an integer.");
        }

        return integer;
    }

    private static void RequireObject(JsonElement value, string collection)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new ManifestValidationException($"Manifest elements in '{collection}' must be objects.");
        }
    }

    private static void RequireString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new ManifestValidationException($"The manifest field '{name}' is required and must be a string.");
        }
    }

    private static void RequireNullableString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
        {
            throw new ManifestValidationException($"The manifest field '{name}' is required and must be a string or null.");
        }
    }

    private static void RequireStringArray(JsonElement parent, string name)
    {
        foreach (var value in RequiredArray(parent, name).EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String)
            {
                throw new ManifestValidationException($"Manifest elements in '{name}' must be strings.");
            }
        }
    }

    private static void Validate(ManifestDocument manifest)
    {
        if (manifest.SchemaVersion != 1)
        {
            throw new ManifestValidationException("The manifest schemaVersion is unsupported; only schemaVersion 1 is accepted.");
        }

        if (manifest.Projects is null || manifest.Events is null || manifest.Unsupported is null || manifest.AnalysisIssues is null || manifest.CompilationDiagnosticKinds is null || manifest.WorkspaceDiagnosticKinds is null)
        {
            throw new ManifestValidationException("The manifest is missing required schema v1 fields.");
        }

        if (manifest.Projects.Count > MaxItems || manifest.Events.Count > MaxItems || manifest.Unsupported.Count > MaxItems || manifest.AnalysisIssues.Count > MaxItems || manifest.CompilationDiagnosticKinds.Count > MaxItems || manifest.WorkspaceDiagnosticKinds.Count > MaxItems)
        {
            throw new ManifestValidationException("The manifest contains more records than the safety limit permits.");
        }

        if (manifest.Projects.Any(project => project is null) || manifest.Events.Any(@event => @event is null) || manifest.Unsupported.Any(item => item is null) || manifest.AnalysisIssues.Any(issue => issue is null))
        {
            throw new ManifestValidationException("The manifest contains null records.");
        }

        var projectKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in manifest.Projects)
        {
            if (string.IsNullOrWhiteSpace(project.Key) || string.IsNullOrWhiteSpace(project.Name) || string.IsNullOrWhiteSpace(project.Assembly) || string.IsNullOrWhiteSpace(project.TargetFramework) || !string.Equals(project.Key, project.Assembly + "|" + project.TargetFramework, StringComparison.Ordinal) || !projectKeys.Add(project.Key))
            {
                throw new ManifestValidationException("A manifest project identity is incomplete or ambiguous.");
            }
        }

        var eventKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var @event in manifest.Events)
        {
            if (string.IsNullOrWhiteSpace(@event.ProjectKey) || !projectKeys.Contains(@event.ProjectKey) || string.IsNullOrWhiteSpace(@event.Identity) || @event.Identity.Contains("global::", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(@event.ContainingType) || string.IsNullOrWhiteSpace(@event.Method) || @event.GenericArity < 0 || @event.ParameterRefKinds is null || @event.ParameterRefKinds.Count == 0 || @event.ParameterForms is null || @event.ParameterForms.Count == 0 || @event.Placeholders is null || @event.Placeholders.Count > MaxItems || @event.Parameters is null || @event.Parameters.Count == 0 || @event.StructuredState is null || @event.StructuredState.Count > @event.Parameters.Count || string.IsNullOrWhiteSpace(@event.EventName) || string.IsNullOrEmpty(@event.Message) || !IsEffectiveLevel(@event.Level) || !IsLevelSource(@event.LevelSource))
            {
                throw new ManifestValidationException("A manifest event is incomplete, non-canonical, or unrelated to a manifest project.");
            }

            var parsedIdentity = ParseMethodIdentity(@event.Identity);
            if (!eventKeys.Add(@event.ProjectKey + "\u001f" + CreateComparisonIdentity(parsedIdentity)))
            {
                throw new ManifestValidationException("A manifest event identity is duplicate or ambiguous.");
            }

            if (!string.Equals(parsedIdentity.ContainingType, @event.ContainingType, StringComparison.Ordinal) ||
                !string.Equals(parsedIdentity.Method, @event.Method, StringComparison.Ordinal) ||
                parsedIdentity.GenericArity != @event.GenericArity ||
                parsedIdentity.Parameters.Count != @event.ParameterRefKinds.Count ||
                !parsedIdentity.Parameters.Select(parameter => parameter.RefKind).SequenceEqual(@event.ParameterRefKinds, StringComparer.Ordinal))
            {
                throw new ManifestValidationException("A manifest event identity contradicts its canonical event fields.");
            }

            var embeddedForms = parsedIdentity.Parameters.Select(parameter => parameter.Form).ToArray();
            var requiredForms = parsedIdentity.Parameters.Select(parameter => GetRequiredParameterForm(parameter.Type)).ToArray();
            if (@event.ParameterRefKinds.Any(kind => !ParameterRefKinds.Contains(kind)) ||
                @event.ParameterForms.Count != parsedIdentity.Parameters.Count ||
                @event.ParameterForms.Any(form => !ParameterForms.Contains(form)) ||
                !embeddedForms.SequenceEqual(requiredForms, StringComparer.Ordinal) ||
                !@event.ParameterForms.SequenceEqual(requiredForms, StringComparer.Ordinal))
            {
                throw new ManifestValidationException("A manifest event parameter form contradicts the required form for its canonical declared type.");
            }

            ValidateEffectiveParameterModel(@event, parsedIdentity);

            foreach (var placeholder in @event.Placeholders)
            {
                if (placeholder is null || string.IsNullOrWhiteSpace(placeholder.Name) || string.IsNullOrWhiteSpace(placeholder.Token))
                {
                    throw new ManifestValidationException("A manifest event contains an incomplete placeholder.");
                }
            }

            ValidateSource(@event.Source);
        }

        foreach (var item in manifest.Unsupported)
        {
            if (string.IsNullOrWhiteSpace(item.ProjectKey) || !projectKeys.Contains(item.ProjectKey) || string.IsNullOrWhiteSpace(item.Declaration) || string.IsNullOrWhiteSpace(item.DeclarationKey) || string.IsNullOrWhiteSpace(item.Reason))
            {
                throw new ManifestValidationException("A manifest unsupported declaration is incomplete or unrelated to a manifest project.");
            }

            ValidateSource(item.Source);
        }

        foreach (var issue in manifest.AnalysisIssues)
        {
            if (string.IsNullOrWhiteSpace(issue.ProjectKey) || !projectKeys.Contains(issue.ProjectKey) || string.IsNullOrWhiteSpace(issue.Code) || string.IsNullOrWhiteSpace(issue.Severity) || !issue.Severity.Equals("error", StringComparison.OrdinalIgnoreCase) && !issue.Severity.Equals("warning", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(issue.Message) || issue.DeclarationKey is null || issue.Sources is null || issue.Sources.Count > MaxItems)
            {
                throw new ManifestValidationException("A manifest analysis issue is incomplete or unrelated to a manifest project.");
            }
            foreach (var source in issue.Sources)
            {
                ValidateSource(source);
            }
        }

        if (manifest.CompilationDiagnosticKinds.Any(kind => string.IsNullOrWhiteSpace(kind)) || manifest.WorkspaceDiagnosticKinds.Any(kind => string.IsNullOrWhiteSpace(kind)))
        {
            throw new ManifestValidationException("A manifest diagnostic-kind list contains an empty value.");
        }
    }

    private static bool IsEffectiveLevel(string? level) =>
        string.Equals(level, "Dynamic", StringComparison.Ordinal) ||
        LoggerMessageGeneratorSemantics.IsFixedLevel(level);

    private static bool IsLevelSource(string? source) => source is "Fixed" or "Dynamic";

    private static void ValidateEffectiveParameterModel(EventContract @event, ParsedMethodIdentity identity)
    {
        if (@event.Parameters.Count != identity.Parameters.Count || @event.ParameterRefKinds.Count != @event.Parameters.Count || @event.ParameterForms.Count != @event.Parameters.Count)
        {
            throw new ManifestValidationException("A manifest event effective parameter model does not match its canonical identity.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < @event.Parameters.Count; index++)
        {
            var parameter = @event.Parameters[index];
            var identityParameter = identity.Parameters[index];
            var codeName = parameter.CodeName ?? parameter.Name;
            if (string.IsNullOrWhiteSpace(parameter.Name) || !IsCanonicalIdentifier(parameter.Name) ||
                string.IsNullOrWhiteSpace(codeName) || !IsCanonicalCodeIdentifier(codeName) || !names.Add(parameter.Name) ||
                !string.Equals(parameter.Type, identityParameter.Type, StringComparison.Ordinal) ||
                !string.Equals(parameter.RefKind, identityParameter.RefKind, StringComparison.Ordinal) ||
                !ParameterRefKinds.Contains(parameter.RefKind))
            {
                throw new ManifestValidationException("A manifest event effective parameter model contains an invalid or contradictory parameter.");
            }

            if (!IsRoleCompatibleWithCanonicalType(parameter.Role, parameter.Type))
            {
                throw new ManifestValidationException("A manifest event parameter role contradicts its canonical declared type.");
            }
        }

        if (!LoggerMessageGeneratorSemantics.TryCreatePersistedModel(
                @event.Parameters,
                @event.LoggerParameter,
                @event.ExceptionParameter,
                @event.LevelSource,
                @event.Level,
                @event.LevelParameter,
                out var semantics,
                out var semanticsReason))
        {
            throw new ManifestValidationException(semanticsReason!);
        }

        var dynamicLevel = string.Equals(@event.LevelSource, "Dynamic", StringComparison.Ordinal);
        if (!LoggerMessageGeneratorSemantics.TryValidateTemplate(semantics, dynamicLevel, @event.Message, @event.Placeholders, out var templateReason))
        {
            throw new ManifestValidationException(templateReason!);
        }

        var parameterIndexes = @event.Parameters.Select((parameter, index) => (parameter.Name, index)).ToDictionary(item => item.Name, item => item.index, StringComparer.Ordinal);
        var expectedStateParameters = @event.Parameters
            .Where(parameter => semantics.IsStructuredState(parameter.Name, dynamicLevel, @event.Placeholders))
            .Select(parameter => parameter.Name)
            .ToHashSet(StringComparer.Ordinal);
        var structuredNames = new HashSet<string>(StringComparer.Ordinal);
        var lastIndex = -1;
        foreach (var property in @event.StructuredState)
        {
            if (string.IsNullOrWhiteSpace(property.ParameterName) || string.IsNullOrWhiteSpace(property.EmittedName) || !structuredNames.Add(property.ParameterName) ||
                !parameterIndexes.TryGetValue(property.ParameterName, out var index) || index <= lastIndex ||
                !semantics.IsStructuredState(@event.Parameters[index].Name, dynamicLevel, @event.Placeholders))
            {
                throw new ManifestValidationException("A manifest structured state model contains an invalid or out-of-order property.");
            }

            var parameter = @event.Parameters[index];
            var expectedEmittedName = LoggerMessageGeneratorSemantics.EmittedName(parameter.Name, parameter.CodeName ?? parameter.Name, @event.Placeholders, expectedStateParameters.Count);
            if (!string.Equals(property.EmittedName, expectedEmittedName, StringComparison.Ordinal))
            {
                throw new ManifestValidationException("A manifest structured state model has an emitted name that contradicts its template or parameter name.");
            }

            lastIndex = index;
        }

        if (!expectedStateParameters.SetEquals(structuredNames))
        {
            throw new ManifestValidationException("A manifest structured state model does not match the effective state parameter set.");
        }
    }

    private static ParsedMethodIdentity ParseMethodIdentity(string identity)
    {
        var aritySeparator = identity.LastIndexOf('`');
        var openingParenthesis = aritySeparator >= 0 ? identity.IndexOf('(', aritySeparator + 1) : -1;
        if (openingParenthesis < 0 || identity[^1] != ')')
        {
            throw new ManifestValidationException("A manifest event contains a malformed method identity.");
        }

        var methodSeparator = aritySeparator > 0 ? identity.LastIndexOf('.', aritySeparator - 1) : -1;
        if (methodSeparator <= 0 || aritySeparator <= methodSeparator + 1 || openingParenthesis <= aritySeparator + 1)
        {
            throw new ManifestValidationException("A manifest event contains a malformed method identity.");
        }

        var containingType = identity[..methodSeparator];
        var method = identity[(methodSeparator + 1)..aritySeparator];
        var arityText = identity[(aritySeparator + 1)..openingParenthesis];
        if (containingType.Length == 0 || method.Length == 0 ||
            !string.Equals(containingType, containingType.Trim(), StringComparison.Ordinal) ||
            !string.Equals(method, method.Trim(), StringComparison.Ordinal) ||
            !TryParseCanonicalType(containingType, out _) || !IsCanonicalIdentifier(method) ||
            !int.TryParse(arityText, out var genericArity))
        {
            throw new ManifestValidationException("A manifest event contains a malformed method identity.");
        }

        if (genericArity < 0 || !string.Equals(arityText, genericArity.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            throw new ManifestValidationException("A manifest event contains a malformed method identity.");
        }

        var parameters = ParseIdentityParameters(identity[(openingParenthesis + 1)..^1]);
        var canonical = containingType + "." + method + "`" + genericArity.ToString(System.Globalization.CultureInfo.InvariantCulture) + "(" + string.Join(",", parameters.Select(parameter => parameter.RefKind + ":" + parameter.Form + ":" + parameter.Type)) + ")";
        if (!string.Equals(identity, canonical, StringComparison.Ordinal))
        {
            throw new ManifestValidationException("A manifest event contains a non-canonical method identity.");
        }

        return new ParsedMethodIdentity(containingType, method, genericArity, parameters);
    }

    internal static string GetComparisonIdentity(string identity) => CreateComparisonIdentity(ParseMethodIdentity(identity));

    private static string CreateComparisonIdentity(ParsedMethodIdentity identity) =>
        identity.ContainingType + "." + identity.Method + "`" + identity.GenericArity.ToString(System.Globalization.CultureInfo.InvariantCulture) + "(" + string.Join(",", identity.Parameters.Select(parameter => parameter.RefKind + ":" + parameter.Type)) + ")";

    internal static string GetRequiredParameterForm(string type)
    {
        if (!TryGetRequiredParameterForm(type, out var form))
        {
            throw new ManifestValidationException("A manifest event contains a non-canonical declared type.");
        }

        return form;
    }

    internal static bool TryGetRequiredParameterForm(string type, out string form)
    {
        form = "None";
        if (!TryParseCanonicalType(type, out var syntax))
        {
            return false;
        }

        form = ClassifyParameterForm(syntax);
        return true;
    }

    private static bool TryParseCanonicalType(string value, out TypeSyntax syntax)
    {
        syntax = null!;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var parsed = SyntaxFactory.ParseTypeName(value);
        if (parsed.ContainsDiagnostics || parsed.DescendantNodesAndSelf().OfType<AliasQualifiedNameSyntax>().Any(alias => !string.Equals(alias.Alias.Identifier.ValueText, "global", StringComparison.Ordinal)))
        {
            return false;
        }

        var canonical = (TypeSyntax)new CanonicalTypeRewriter().Visit(parsed.WithoutTrivia())!;
        if (!string.Equals(value, canonical.ToFullString(), StringComparison.Ordinal))
        {
            return false;
        }

        syntax = canonical;
        return true;
    }

    private static string ClassifyParameterForm(TypeSyntax syntax)
    {
        if (!TryGetNamedTypeSegments(syntax, out var segments))
        {
            return "None";
        }

        if (MatchesNamedType(segments, ["Microsoft", "Extensions", "Logging"], "ILogger", 0, 1))
        {
            return "ILogger";
        }

        if (MatchesNamedType(segments, ["Microsoft", "Extensions", "Logging"], "LogLevel", 0))
        {
            return "LogLevel";
        }

        if (MatchesNamedType(segments, ["System"], "Exception", 0))
        {
            return "Exception";
        }

        return "None";
    }

    private static bool TryGetNamedTypeSegments(TypeSyntax syntax, out IReadOnlyList<NamedTypeSegment> segments)
    {
        var result = new List<NamedTypeSegment>();
        if (syntax is not NameSyntax name || !AppendNamedTypeSegments(name, result))
        {
            segments = Array.Empty<NamedTypeSegment>();
            return false;
        }

        segments = result;
        return true;
    }

    private static bool AppendNamedTypeSegments(NameSyntax name, List<NamedTypeSegment> segments)
    {
        switch (name)
        {
            case QualifiedNameSyntax qualified:
                return AppendNamedTypeSegments(qualified.Left, segments) && AppendNamedTypeSegment(qualified.Right, segments);
            case AliasQualifiedNameSyntax:
                return false;
            default:
                return name is SimpleNameSyntax simple && AppendNamedTypeSegment(simple, segments);
        }
    }

    private static bool AppendNamedTypeSegment(SimpleNameSyntax name, List<NamedTypeSegment> segments)
    {
        switch (name)
        {
            case IdentifierNameSyntax identifier:
                segments.Add(new NamedTypeSegment(identifier.Identifier.ValueText, 0));
                return true;
            case GenericNameSyntax generic:
                segments.Add(new NamedTypeSegment(generic.Identifier.ValueText, generic.TypeArgumentList.Arguments.Count));
                return true;
            default:
                return false;
        }
    }

    private static bool MatchesNamedType(IReadOnlyList<NamedTypeSegment> segments, IReadOnlyList<string> namespaceSegments, string typeName, params int[] acceptedArities)
    {
        if (segments.Count != namespaceSegments.Count + 1 || !acceptedArities.Contains(segments[^1].Arity))
        {
            return false;
        }

        for (var index = 0; index < namespaceSegments.Count; index++)
        {
            if (segments[index].Arity != 0 || !string.Equals(segments[index].Name, namespaceSegments[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return string.Equals(segments[^1].Name, typeName, StringComparison.Ordinal);
    }

    private static IReadOnlyList<ParsedParameterIdentity> ParseIdentityParameters(string value)
    {
        if (value.Length == 0)
        {
            return Array.Empty<ParsedParameterIdentity>();
        }

        var parameters = new List<ParsedParameterIdentity>();
        var start = 0;
        var angleDepth = 0;
        var bracketDepth = 0;
        var parenthesisDepth = 0;
        for (var index = 0; index <= value.Length; index++)
        {
            if (index < value.Length)
            {
                switch (value[index])
                {
                    case '<': angleDepth++; break;
                    case '>': angleDepth--; break;
                    case '[': bracketDepth++; break;
                    case ']': bracketDepth--; break;
                    case '(': parenthesisDepth++; break;
                    case ')': parenthesisDepth--; break;
                }

                if (angleDepth < 0 || bracketDepth < 0 || parenthesisDepth < 0)
                {
                    throw new ManifestValidationException("A manifest event contains a malformed method identity.");
                }
            }

            if (index != value.Length && (value[index] != ',' || angleDepth != 0 || bracketDepth != 0 || parenthesisDepth != 0))
            {
                continue;
            }

            if (angleDepth != 0 || bracketDepth != 0 || parenthesisDepth != 0)
            {
                throw new ManifestValidationException("A manifest event contains a malformed method identity.");
            }

            var parameter = value[start..index];
            var refKindSeparator = parameter.IndexOf(':');
            var formSeparator = refKindSeparator < 0 ? -1 : parameter.IndexOf(':', refKindSeparator + 1);
            if (refKindSeparator <= 0 || formSeparator <= refKindSeparator + 1 || formSeparator == parameter.Length - 1)
            {
                throw new ManifestValidationException("A manifest event contains a malformed method identity.");
            }

            var refKind = parameter[..refKindSeparator];
            var form = parameter[(refKindSeparator + 1)..formSeparator];
            var type = parameter[(formSeparator + 1)..];
            if (!ParameterRefKinds.Contains(refKind) || !ParameterForms.Contains(form) || !string.Equals(type, type.Trim(), StringComparison.Ordinal) || !TryGetRequiredParameterForm(type, out _))
            {
                throw new ManifestValidationException("A manifest event contains a malformed method identity.");
            }

            parameters.Add(new ParsedParameterIdentity(refKind, form, type));
            start = index + 1;
        }

        return parameters;
    }

    private static bool IsCanonicalIdentifier(string value) =>
        SyntaxFacts.IsValidIdentifier(value) || SyntaxFacts.GetKeywordKind(value) != SyntaxKind.None;

    private static bool IsCanonicalCodeIdentifier(string value)
    {
        var semanticName = value.StartsWith('@') ? value[1..] : value;
        return semanticName.Length > 0 && value.Count(character => character == '@') <= 1 &&
            (SyntaxFacts.IsValidIdentifier(semanticName) || SyntaxFacts.GetKeywordKind(semanticName) != SyntaxKind.None);
    }

    private static bool IsRoleCompatibleWithCanonicalType(string role, string type)
    {
        if (!LoggerMessageGeneratorSemantics.TryParseRole(role, out var roles))
        {
            return false;
        }

        var requiredForm = GetRequiredParameterForm(type);
        if (roles.HasFlag(GeneratorParameterRoles.Logger) && requiredForm is "Exception" or "LogLevel" ||
            roles.HasFlag(GeneratorParameterRoles.Exception) && requiredForm is "ILogger" or "LogLevel" ||
            roles.HasFlag(GeneratorParameterRoles.LogLevel) && requiredForm is "ILogger" or "Exception")
        {
            return false;
        }

        return requiredForm != "None" || !IsKnownNonInheritableType(type) || roles == GeneratorParameterRoles.None;
    }

    private static bool IsKnownNonInheritableType(string type) => type is
        "bool" or "byte" or "sbyte" or "char" or "decimal" or "double" or "float" or "int" or "uint" or
        "long" or "ulong" or "nint" or "nuint" or "short" or "ushort" or "string" or "object" or
        "System.Boolean" or "System.Byte" or "System.SByte" or "System.Char" or "System.Decimal" or
        "System.Double" or "System.Single" or "System.Int32" or "System.UInt32" or "System.Int64" or
        "System.UInt64" or "System.IntPtr" or "System.UIntPtr" or "System.Int16" or "System.UInt16" or
        "System.String" or "System.Object";

    private static void ValidateJsonSource(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var source) || source.ValueKind != JsonValueKind.Object)
        {
            throw new ManifestValidationException($"The manifest field '{name}' is required and must be an object.");
        }
        RejectUnknownProperties(source, "source", "file", "line", "kind");
        RequireString(source, "file");
        _ = RequiredInteger(source, "line");
        RequireString(source, "kind");
    }

    private static void ValidateSource(SourceLocation? source)
    {
        if (source is null || string.IsNullOrWhiteSpace(source.File) || source.Line < 1 || source.Kind is not ("source" or "generated") || Path.IsPathRooted(source.File) || source.File.Contains('\\', StringComparison.Ordinal) || source.File.Contains(':', StringComparison.Ordinal) || source.File.Contains("//", StringComparison.Ordinal) || source.File.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new ManifestValidationException("A manifest source location is incomplete or contains an absolute or non-canonical path.");
        }
    }

    private sealed class CanonicalTypeRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitAliasQualifiedName(AliasQualifiedNameSyntax node)
        {
            if (string.Equals(node.Alias.Identifier.ValueText, "global", StringComparison.Ordinal))
            {
                return Visit(node.Name);
            }

            return base.VisitAliasQualifiedName(node);
        }

        public override SyntaxToken VisitToken(SyntaxToken token)
        {
            return token.IsKind(SyntaxKind.IdentifierToken)
                ? SyntaxFactory.Identifier(token.ValueText)
                : token;
        }
    }

    private sealed record NamedTypeSegment(string Name, int Arity);
    private sealed record ParsedMethodIdentity(string ContainingType, string Method, int GenericArity, IReadOnlyList<ParsedParameterIdentity> Parameters);

    private sealed record ParsedParameterIdentity(string RefKind, string Form, string Type);
}

internal sealed class ManifestValidationException(string message) : Exception(message);

internal sealed class ManifestReadException(string message) : Exception(message);

internal sealed class ManifestWriteException(string message) : Exception(message);
