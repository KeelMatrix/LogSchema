using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis.CSharp;

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

    internal static async Task WriteAsync(ManifestDocument manifest, string path, CancellationToken cancellationToken)
    {
        string? temporaryPath = null;
        try
        {
            Validate(manifest);
            var canonical = manifest.Canonicalize();
            Validate(canonical);
            var json = JsonSerializer.Serialize(canonical, Options)
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
        return manifest;
    }

    private static void ValidateJsonShape(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ManifestValidationException("The manifest root must be an object.");
        }

        _ = RequiredInteger(root, "schemaVersion");
        foreach (var name in new[] { "projects", "events", "unsupported", "analysisIssues", "compilationDiagnosticKinds", "workspaceDiagnosticKinds" })
        {
            _ = RequiredArray(root, name);
        }

        foreach (var project in RequiredArray(root, "projects").EnumerateArray())
        {
            RequireObject(project, "projects");
            RequireString(project, "key");
            RequireString(project, "name");
            RequireString(project, "assembly");
            RequireString(project, "targetFramework");
        }

        foreach (var @event in RequiredArray(root, "events").EnumerateArray())
        {
            RequireObject(@event, "events");
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
                RequireString(placeholder, "name");
                RequireString(placeholder, "token");
            }
            RequireStringArray(@event, "parameterForms");
            ValidateJsonSource(@event, "source");
        }

        foreach (var item in RequiredArray(root, "unsupported").EnumerateArray())
        {
            RequireObject(item, "unsupported");
            RequireString(item, "projectKey");
            ValidateJsonSource(item, "source");
            RequireString(item, "declaration");
            RequireString(item, "declarationKey");
            RequireString(item, "reason");
        }

        foreach (var issue in RequiredArray(root, "analysisIssues").EnumerateArray())
        {
            RequireObject(issue, "analysisIssues");
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
                RequireString(source, "file");
                _ = RequiredInteger(source, "line");
                RequireString(source, "kind");
            }
        }

        RequireStringArray(root, "compilationDiagnosticKinds");
        RequireStringArray(root, "workspaceDiagnosticKinds");
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
            if (string.IsNullOrWhiteSpace(@event.ProjectKey) || !projectKeys.Contains(@event.ProjectKey) || string.IsNullOrWhiteSpace(@event.Identity) || @event.Identity.Contains("global::", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(@event.ContainingType) || string.IsNullOrWhiteSpace(@event.Method) || @event.GenericArity < 0 || @event.ParameterRefKinds is null || @event.ParameterRefKinds.Count == 0 || @event.ParameterForms is null || @event.ParameterForms.Count == 0 || @event.Placeholders is null || @event.Placeholders.Count > MaxItems || string.IsNullOrWhiteSpace(@event.EventName) || string.IsNullOrEmpty(@event.Message) || !IsEffectiveLevel(@event.Level))
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

            if (@event.ParameterRefKinds.Any(kind => !ParameterRefKinds.Contains(kind)) ||
                @event.ParameterForms.Count != parsedIdentity.Parameters.Count ||
                @event.ParameterForms.Any(form => !ParameterForms.Contains(form)) ||
                !parsedIdentity.Parameters.Select(parameter => parameter.Form).SequenceEqual(@event.ParameterForms, StringComparer.Ordinal))
            {
                throw new ManifestValidationException("A manifest event parameter form contradicts its canonical method identity.");
            }

            var hasLoggerParameter = false;
            for (var index = 0; index < parsedIdentity.Parameters.Count; index++)
            {
                var parameterType = parsedIdentity.Parameters[index].Type;
                var form = parsedIdentity.Parameters[index].Form;
                if (IsLoggerType(parameterType))
                {
                    hasLoggerParameter = true;
                    if (form != "ILogger") throw new ManifestValidationException("A manifest event parameter form contradicts its canonical method identity.");
                }
                else if (parameterType == "Microsoft.Extensions.Logging.LogLevel")
                {
                    if (form != "LogLevel") throw new ManifestValidationException("A manifest event parameter form contradicts its canonical method identity.");
                }
                else if (parameterType == "System.Exception")
                {
                    if (form != "Exception") throw new ManifestValidationException("A manifest event parameter form contradicts its canonical method identity.");
                }
                else if (form == "ILogger" || form == "LogLevel" || form == "Exception" && IsKnownNonExceptionType(parameterType))
                {
                    throw new ManifestValidationException("A manifest event parameter form contradicts its canonical method identity.");
                }
            }
            if (!hasLoggerParameter)
            {
                throw new ManifestValidationException("A manifest event parameter form contradicts its canonical method identity.");
            }

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

    private static bool IsEffectiveLevel(string? level) => level is "Trace" or "Debug" or "Information" or "Warning" or "Error" or "Critical" or "None" or "Dynamic" || int.TryParse(level, out _);

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
            !IsCanonicalTypeSyntax(containingType) || !IsCanonicalIdentifier(method) ||
            !int.TryParse(arityText, out var genericArity) || genericArity < 0 ||
            !string.Equals(arityText, genericArity.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))
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

    private static bool IsLoggerType(string type) =>
        type == "Microsoft.Extensions.Logging.ILogger" ||
        type.StartsWith("Microsoft.Extensions.Logging.ILogger<", StringComparison.Ordinal) && type.EndsWith('>');

    private static bool IsKnownNonExceptionType(string type)
    {
        var normalized = type.EndsWith('?') ? type[..^1] : type;
        return normalized is "bool" or "byte" or "sbyte" or "short" or "ushort" or "int" or "uint" or "long" or "ulong" or "nint" or "nuint" or "char" or "float" or "double" or "decimal" or "string" or "object" or "dynamic" or
            "System.Boolean" or "System.Byte" or "System.SByte" or "System.Int16" or "System.UInt16" or "System.Int32" or "System.UInt32" or "System.Int64" or "System.UInt64" or "System.IntPtr" or "System.UIntPtr" or "System.Char" or "System.Single" or "System.Double" or "System.Decimal" or "System.String" or "System.Object";
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
            if (!ParameterRefKinds.Contains(refKind) || !ParameterForms.Contains(form) || !string.Equals(type, type.Trim(), StringComparison.Ordinal) || type.Contains("global::", StringComparison.Ordinal) || !IsCanonicalTypeSyntax(type))
            {
                throw new ManifestValidationException("A manifest event contains a malformed method identity.");
            }

            parameters.Add(new ParsedParameterIdentity(refKind, form, type));
            start = index + 1;
        }

        return parameters;
    }

    private static bool IsCanonicalTypeSyntax(string value)
    {
        var syntax = SyntaxFactory.ParseTypeName(value);
        return !syntax.ContainsDiagnostics && syntax.FullSpan.Length == value.Length && string.Equals(syntax.ToFullString(), value, StringComparison.Ordinal);
    }

    private static bool IsCanonicalIdentifier(string value) =>
        SyntaxFacts.IsValidIdentifier(value) || SyntaxFacts.GetKeywordKind(value) != SyntaxKind.None;

    private static void ValidateJsonSource(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var source) || source.ValueKind != JsonValueKind.Object)
        {
            throw new ManifestValidationException($"The manifest field '{name}' is required and must be an object.");
        }
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

    private sealed record ParsedMethodIdentity(string ContainingType, string Method, int GenericArity, IReadOnlyList<ParsedParameterIdentity> Parameters);

    private sealed record ParsedParameterIdentity(string RefKind, string Form, string Type);
}

internal sealed class ManifestValidationException(string message) : Exception(message);

internal sealed class ManifestReadException(string message) : Exception(message);

internal sealed class ManifestWriteException(string message) : Exception(message);
