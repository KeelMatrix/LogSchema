using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;

if (args.Length is < 3 or > 5 || args[1] != "--output")
{
    Console.Error.WriteLine("Usage: Phase0.LogSchemaProbe <project> --output <manifest> [--tfm <target-framework>]");
    return 2;
}

var projectPath = Path.GetFullPath(args[0]);
var outputPath = Path.GetFullPath(args[2]);
var targetFramework = GetOption(args, "--tfm") ?? "unspecified";
var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default
};

if (!File.Exists(projectPath))
{
    Console.Error.WriteLine($"Project not found: {projectPath}");
    return 3;
}

if (!MSBuildLocator.IsRegistered)
{
    MSBuildLocator.RegisterDefaults();
}

var workspaceDiagnostics = new List<string>();
using var workspace = MSBuildWorkspace.Create(new Dictionary<string, string>
{
    ["TargetFramework"] = targetFramework == "unspecified" ? string.Empty : targetFramework,
    ["DesignTimeBuild"] = "true",
    ["BuildingProject"] = "false"
});
workspace.RegisterWorkspaceFailedHandler(eventArgs => workspaceDiagnostics.Add($"{eventArgs.Diagnostic.Kind}:{eventArgs.Diagnostic.Message}"));

Project project;
try
{
    project = await workspace.OpenProjectAsync(projectPath);
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Project load failed: {exception.GetType().Name}: {exception.Message}");
    return 3;
}

Compilation? compilation;
try
{
    compilation = await project.GetCompilationAsync();
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Compilation load failed: {exception.GetType().Name}: {exception.Message}");
    return 3;
}

if (compilation is null)
{
    Console.Error.WriteLine("Compilation load failed: no compilation was returned.");
    return 3;
}

var extractor = new Extractor(project, compilation);
var manifest = await extractor.ExtractAsync(targetFramework, workspaceDiagnostics);
var json = JsonSerializer.Serialize(manifest, jsonOptions)
    .Replace("\r\n", "\n", StringComparison.Ordinal)
    .Replace('\r', '\n');
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
await File.WriteAllTextAsync(outputPath, json + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

Console.WriteLine($"Loaded {manifest.Project.Assembly} ({manifest.Project.TargetFramework}).");
Console.WriteLine($"Extracted {manifest.Events.Count} declarations; reported {manifest.Unsupported.Count} unsupported declarations.");
Console.WriteLine($"Wrote canonical manifest: {Path.GetFileName(outputPath)}");
return 0;

static string? GetOption(string[] arguments, string name)
{
    var index = Array.IndexOf(arguments, name);
    return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
}

internal sealed class Extractor
{
    private const string LoggerMessageAttributeName = "Microsoft.Extensions.Logging.LoggerMessageAttribute";
    private readonly Project project;
    private readonly Compilation compilation;
    private readonly List<EventContract> events = [];
    private readonly List<UnsupportedDeclaration> unsupported = [];
    private readonly List<GeneratedImplementationReplica> generatedImplementationReplicas = [];
    private readonly HashSet<SyntaxTree> scannedTrees = [];
    private readonly Dictionary<string, List<SourceLocation>> projectSourceDeclarations = new(StringComparer.Ordinal);
    private readonly HashSet<string> pairedGeneratedDeclarations = new(StringComparer.Ordinal);

    internal Extractor(Project project, Compilation compilation)
    {
        this.project = project;
        this.compilation = compilation;
    }

    internal async Task<ContractManifest> ExtractAsync(string targetFramework, IReadOnlyList<string> workspaceDiagnostics)
    {
        foreach (var document in project.Documents.OrderBy(document => document.Name, StringComparer.Ordinal))
        {
            var root = await document.GetSyntaxRootAsync();
            var model = await document.GetSemanticModelAsync();
            if (root is not null && model is not null)
            {
                scannedTrees.Add(root.SyntaxTree);
                Scan(root, model, Path.GetFileName(document.FilePath ?? document.Name) ?? document.Name, isProjectSource: true);
            }
        }

        foreach (var tree in compilation.SyntaxTrees.OrderBy(tree => Path.GetFileName(tree.FilePath), StringComparer.Ordinal))
        {
            if (!scannedTrees.Add(tree))
            {
                continue;
            }

            Scan(tree.GetRoot(), compilation.GetSemanticModel(tree), Path.GetFileName(tree.FilePath) ?? "generated.cs", isProjectSource: false);
        }

        var diagnosticIds = compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error || diagnostic.Severity == DiagnosticSeverity.Warning)
            .Select(diagnostic => $"{diagnostic.Id}:{diagnostic.Severity}")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        events.Sort(EventContractComparer.Instance);
        unsupported.Sort(UnsupportedComparer.Instance);
        generatedImplementationReplicas.Sort(GeneratedImplementationReplicaComparer.Instance);

        return new ContractManifest(
            SchemaVersion: 1,
            Project: new ProjectIdentity(compilation.AssemblyName ?? project.Name, targetFramework),
            Events: events,
            Unsupported: unsupported,
            GeneratedImplementationReplicas: generatedImplementationReplicas,
            CompilationDiagnosticKinds: diagnosticIds,
            WorkspaceDiagnosticKinds: workspaceDiagnostics
                .Select(value => value.Split(':', 2)[0])
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray());
    }

    private void Scan(SyntaxNode root, SemanticModel model, string documentName, bool isProjectSource)
    {
        foreach (var methodSyntax in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var methodSymbol = model.GetDeclaredSymbol(methodSyntax);
            var attribute = methodSymbol?.GetAttributes().FirstOrDefault(attribute =>
                string.Equals(attribute.AttributeClass?.ToDisplayString(), LoggerMessageAttributeName, StringComparison.Ordinal));

            if (methodSymbol is null || attribute is null)
            {
                continue;
            }

            var source = new SourceLocation(
                File: Path.GetFileName(documentName),
                Line: methodSyntax.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                Kind: isProjectSource ? "source" : "generated");

            var declarationKey = GetDeclarationKey(methodSymbol);
            if (isProjectSource)
            {
                if (!projectSourceDeclarations.TryGetValue(declarationKey, out var sourceDeclarations))
                {
                    sourceDeclarations = [];
                    projectSourceDeclarations.Add(declarationKey, sourceDeclarations);
                }

                sourceDeclarations.Add(source);
            }
            else if (IsGeneratedImplementation(methodSymbol))
            {
                if (projectSourceDeclarations.TryGetValue(declarationKey, out var sourceDeclarations) &&
                    sourceDeclarations.Count == 1 &&
                    pairedGeneratedDeclarations.Add(declarationKey))
                {
                    generatedImplementationReplicas.Add(new GeneratedImplementationReplica(
                        Source: source with { Line = 0 },
                        Declaration: NormalizeDeclaration(methodSyntax.ToString()),
                        PairedSource: sourceDeclarations[0],
                        Reason: "compiler-generated LoggerMessage implementation replica paired one-to-one with the project-source declaration"));
                    continue;
                }

                unsupported.Add(new UnsupportedDeclaration(
                    Source: source,
                    Declaration: NormalizeDeclaration(methodSyntax.ToString()),
                    Reason: "generated LoggerMessage declaration is not paired one-to-one with a project-source declaration"));
                continue;
            }

            if (!TryExtract(methodSyntax, methodSymbol, attribute, source, out var contract, out var reason))
            {
                unsupported.Add(new UnsupportedDeclaration(
                    Source: source,
                    Declaration: NormalizeDeclaration(methodSyntax.ToString()),
                    Reason: reason!));
                continue;
            }

            events.Add(contract!);
        }
    }

    private static string GetDeclarationKey(IMethodSymbol method) =>
        method.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "." +
        method.Name + "(" +
        string.Join(",", method.Parameters.Select(parameter => parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))) + ")";

    private static bool TryExtract(
        MethodDeclarationSyntax syntax,
        IMethodSymbol method,
        AttributeData attribute,
        SourceLocation source,
        out EventContract? contract,
        out string? reason)
    {
        contract = null;
        reason = null;

        if (!syntax.Modifiers.Any(SyntaxKind.PartialKeyword))
        {
            reason = "method is not partial; LoggerMessage source generation requires a partial method";
            return false;
        }

        if (syntax.Parent is not TypeDeclarationSyntax containingType || !containingType.Modifiers.Any(SyntaxKind.PartialKeyword))
        {
            reason = "containing type is not partial";
            return false;
        }

        if (!method.ReturnsVoid)
        {
            reason = "method does not return void";
            return false;
        }

        if (method.IsGenericMethod)
        {
            reason = "generic logging methods are outside this bounded probe";
            return false;
        }

        if (!method.Parameters.Any(parameter => IsLogger(parameter.Type)))
        {
            reason = "method has no ILogger parameter";
            return false;
        }

        if (!TryReadAttribute(attribute, method.Name, out var eventId, out var eventName, out var level, out var message))
        {
            reason = "attribute arguments are not compile-time constants in the supported LoggerMessage shape";
            return false;
        }

        if (!TryReadPlaceholders(message, out var placeholders, out reason))
        {
            return false;
        }

        var valueParameters = method.Parameters
            .Where(parameter => !IsLogger(parameter.Type) && !IsException(parameter.Type) && !IsLogLevel(parameter.Type))
            .Select(parameter => parameter.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (placeholders.Any(placeholder => !valueParameters.Contains(placeholder.Name)))
        {
            reason = "message placeholder does not match a non-special method parameter";
            return false;
        }

        var containingTypeName = method.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty, StringComparison.Ordinal);
        var parameterTypes = string.Join(",", method.Parameters.Select(parameter => parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty, StringComparison.Ordinal)));

        contract = new EventContract(
            Identity: $"{containingTypeName}.{method.Name}({parameterTypes})",
            ContainingType: containingTypeName,
            Method: method.Name,
            EventId: eventId,
            EventName: eventName,
            Level: level,
            Message: message,
            Placeholders: placeholders,
            ParameterForms: method.Parameters
                .Where(parameter => IsLogger(parameter.Type) || IsException(parameter.Type) || IsLogLevel(parameter.Type))
                .Select(parameter => IsLogger(parameter.Type) ? "ILogger" : IsException(parameter.Type) ? "Exception" : "LogLevel")
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray(),
            Source: source);
        return true;
    }

    private static bool TryReadAttribute(
        AttributeData attribute,
        string methodName,
        out int eventId,
        out string eventName,
        out string level,
        out string message)
    {
        eventId = -1;
        eventName = methodName;
        level = "None";
        message = string.Empty;

        foreach (var argument in attribute.ConstructorArguments)
        {
            if (argument.Type?.Name == "LogLevel")
            {
                level = EnumName(argument);
            }
            else if (argument.Type?.SpecialType == SpecialType.System_Int32)
            {
                eventId = Convert.ToInt32(argument.Value, CultureInfo.InvariantCulture);
            }
            else if (argument.Type?.SpecialType == SpecialType.System_String)
            {
                message = argument.Value as string ?? string.Empty;
            }
        }

        foreach (var (name, argument) in attribute.NamedArguments)
        {
            switch (name)
            {
                case "EventId" when argument.Type?.SpecialType == SpecialType.System_Int32:
                    eventId = Convert.ToInt32(argument.Value, CultureInfo.InvariantCulture);
                    break;
                case "EventName" when argument.Type?.SpecialType == SpecialType.System_String:
                    eventName = argument.Value as string ?? methodName;
                    break;
                case "Level" when argument.Type?.Name == "LogLevel":
                    level = EnumName(argument);
                    break;
                case "Message" when argument.Type?.SpecialType == SpecialType.System_String:
                    message = argument.Value as string ?? string.Empty;
                    break;
            }
        }

        return !string.IsNullOrEmpty(message);
    }

    private static bool TryReadPlaceholders(string message, out IReadOnlyList<Placeholder> placeholders, out string? reason)
    {
        var values = new List<Placeholder>();
        reason = null;

        for (var index = 0; index < message.Length; index++)
        {
            if (message[index] == '{' && index + 1 < message.Length && message[index + 1] == '{')
            {
                index++;
                continue;
            }

            if (message[index] == '}' && index + 1 < message.Length && message[index + 1] == '}')
            {
                index++;
                continue;
            }

            if (message[index] != '{')
            {
                continue;
            }

            var close = message.IndexOf('}', index + 1);
            if (close < 0)
            {
                placeholders = [];
                reason = "message template contains an unterminated placeholder";
                return false;
            }

            var token = message[(index + 1)..close];
            var separator = token.IndexOfAny([',', ':']);
            var name = (separator < 0 ? token : token[..separator]).Trim();
            if (name.Length == 0)
            {
                placeholders = [];
                reason = "message template contains an empty placeholder";
                return false;
            }

            values.Add(new Placeholder(name, token));
            index = close;
        }

        placeholders = values;
        return true;
    }

    private static string EnumName(TypedConstant argument)
    {
        var numericValue = Convert.ToInt32(argument.Value, CultureInfo.InvariantCulture);
        return numericValue switch
        {
            0 => "Trace",
            1 => "Debug",
            2 => "Information",
            3 => "Warning",
            4 => "Error",
            5 => "Critical",
            6 => "None",
            _ => numericValue.ToString(CultureInfo.InvariantCulture)
        };
    }

    private static bool IsLogger(ITypeSymbol type) =>
        type.Name == "ILogger" && type.ContainingNamespace.ToDisplayString() == "Microsoft.Extensions.Logging";

    private static bool IsGeneratedImplementation(IMethodSymbol method) =>
        method.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() == "System.CodeDom.Compiler.GeneratedCodeAttribute");

    private static bool IsLogLevel(ITypeSymbol type) =>
        type.Name == "LogLevel" && type.ContainingNamespace.ToDisplayString() == "Microsoft.Extensions.Logging";

    private static bool IsException(ITypeSymbol type)
    {
        for (var current = type; current is not null; current = (current as INamedTypeSymbol)?.BaseType)
        {
            if (current.Name == "Exception" && current.ContainingNamespace.ToDisplayString() == "System")
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeDeclaration(string declaration) => declaration.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}

internal sealed record ContractManifest(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("project")] ProjectIdentity Project,
    [property: JsonPropertyName("events")] IReadOnlyList<EventContract> Events,
    [property: JsonPropertyName("unsupported")] IReadOnlyList<UnsupportedDeclaration> Unsupported,
    [property: JsonPropertyName("generatedImplementationReplicas")] IReadOnlyList<GeneratedImplementationReplica> GeneratedImplementationReplicas,
    [property: JsonPropertyName("compilationDiagnosticKinds")] IReadOnlyList<string> CompilationDiagnosticKinds,
    [property: JsonPropertyName("workspaceDiagnosticKinds")] IReadOnlyList<string> WorkspaceDiagnosticKinds);

internal sealed record ProjectIdentity(
    [property: JsonPropertyName("assembly")] string Assembly,
    [property: JsonPropertyName("targetFramework")] string TargetFramework);

internal sealed record EventContract(
    [property: JsonPropertyName("identity")] string Identity,
    [property: JsonPropertyName("containingType")] string ContainingType,
    [property: JsonPropertyName("method")] string Method,
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
    [property: JsonPropertyName("source")] SourceLocation Source,
    [property: JsonPropertyName("declaration")] string Declaration,
    [property: JsonPropertyName("reason")] string Reason);

internal sealed record GeneratedImplementationReplica(
    [property: JsonPropertyName("source")] SourceLocation Source,
    [property: JsonPropertyName("declaration")] string Declaration,
    [property: JsonPropertyName("pairedSource")] SourceLocation PairedSource,
    [property: JsonPropertyName("reason")] string Reason);

internal sealed record SourceLocation(
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("kind")] string Kind);

internal sealed class EventContractComparer : IComparer<EventContract>
{
    internal static readonly EventContractComparer Instance = new();

    public int Compare(EventContract? x, EventContract? y) => StringComparer.Ordinal.Compare(x?.Identity, y?.Identity);
}

internal sealed class UnsupportedComparer : IComparer<UnsupportedDeclaration>
{
    internal static readonly UnsupportedComparer Instance = new();

    public int Compare(UnsupportedDeclaration? x, UnsupportedDeclaration? y)
    {
        var source = StringComparer.Ordinal.Compare(x?.Source.File, y?.Source.File);
        return source != 0 ? source : (x?.Source.Line ?? 0).CompareTo(y?.Source.Line ?? 0);
    }
}

internal sealed class GeneratedImplementationReplicaComparer : IComparer<GeneratedImplementationReplica>
{
    internal static readonly GeneratedImplementationReplicaComparer Instance = new();

    public int Compare(GeneratedImplementationReplica? x, GeneratedImplementationReplica? y)
    {
        var source = StringComparer.Ordinal.Compare(x?.Source.File, y?.Source.File);
        if (source != 0)
        {
            return source;
        }

        return StringComparer.Ordinal.Compare(x?.Declaration, y?.Declaration);
    }
}
