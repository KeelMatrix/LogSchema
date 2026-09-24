using System.Globalization;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;

namespace KeelMatrix.LogSchema;

internal sealed class LogSchemaExtractor
{
    private const string LoggerMessageAttributeName = "Microsoft.Extensions.Logging.LoggerMessageAttribute";
    internal const string DynamicLevelName = "Dynamic";
    internal const string EmptyEventsIssueCode = "KMLOGP006";
    internal const string EmptyEventsIssueMessage = "No supported [LoggerMessage] declarations were found. The project may genuinely have no [LoggerMessage] declarations, may use an unsupported declaration shape, or may target the wrong framework; for a multi-targeted project, try --tfm.";
    internal const string EmptyBaselineEventsIssueMessage = "The baseline manifest contains zero events. No supported [LoggerMessage] declarations may have been captured because the project genuinely has none, uses an unsupported declaration shape, or targets the wrong framework; for a multi-targeted project, try --tfm.";

    internal static async Task<ManifestDocument> ExtractAsync(string inputPath, string? targetFramework, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(inputPath);
        if (!File.Exists(fullPath))
        {
            throw new ProjectAnalysisException("The project or solution file was not found.");
        }

        if (!MSBuildLocator.IsRegistered)
        {
            MSBuildLocator.RegisterDefaults();
        }

        var workspaceDiagnostics = new List<string>();
        var properties = new Dictionary<string, string>
        {
            ["DesignTimeBuild"] = "true",
            ["BuildingProject"] = "false"
        };
        if (!string.IsNullOrWhiteSpace(targetFramework))
        {
            properties["TargetFramework"] = targetFramework;
        }

        using var workspace = MSBuildWorkspace.Create(properties);
        workspace.WorkspaceFailed += (_, eventArgs) => workspaceDiagnostics.Add(eventArgs.Diagnostic.Kind.ToString());

        Project[] projects;
        try
        {
            if (Path.GetExtension(fullPath).Equals(".sln", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(fullPath).Equals(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                var solution = await workspace.OpenSolutionAsync(fullPath, cancellationToken: cancellationToken);
                projects = solution.Projects.Where(project => project.Language == LanguageNames.CSharp).OrderBy(project => project.Name, StringComparer.Ordinal).ToArray();
            }
            else
            {
                projects = [await workspace.OpenProjectAsync(fullPath, cancellationToken: cancellationToken)];
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new ProjectAnalysisException($"Project load failed: {exception.GetType().Name}.");
        }

        if (projects.Length == 0)
        {
            throw new ProjectAnalysisException("Project load failed: no C# projects were found.");
        }

        var allProjects = new List<ProjectIdentity>();
        var allEvents = new List<EventContract>();
        var allUnsupported = new List<UnsupportedDeclaration>();
        var allIssues = new List<AnalysisIssue>();
        var compilationDiagnostics = new HashSet<string>(StringComparer.Ordinal);

        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Compilation? compilation;
            try
            {
                compilation = await project.GetCompilationAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new ProjectAnalysisException($"Compilation load failed for {project.Name}: {exception.GetType().Name}.");
            }

            if (compilation is null)
            {
                throw new ProjectAnalysisException($"Compilation load failed for {project.Name}: no compilation was returned.");
            }

            var actualTargetFramework = string.IsNullOrWhiteSpace(targetFramework) ? "unspecified" : targetFramework;
            var assembly = compilation.AssemblyName ?? project.Name;
            var projectKey = assembly + "|" + actualTargetFramework;
            var identity = new ProjectIdentity(projectKey, project.Name, assembly, actualTargetFramework);
            if (allProjects.Any(existing => string.Equals(existing.Key, identity.Key, StringComparison.Ordinal)))
            {
                allIssues.Add(new AnalysisIssue(projectKey, "KMLOGP003", "error", "Multiple projects share one canonical project identity.", projectKey, Array.Empty<SourceLocation>()));
                continue;
            }

            allProjects.Add(identity);
            var analyzer = new ProjectExtractor(project, compilation, identity);
            var result = await analyzer.ExtractAsync(cancellationToken);
            allEvents.AddRange(result.Events.Select(@event => @event with { ProjectKey = projectKey }));
            allUnsupported.AddRange(result.Unsupported.Select(item => item with { ProjectKey = projectKey }));
            allIssues.AddRange(result.AnalysisIssues.Select(issue => issue with { ProjectKey = projectKey }));
            foreach (var diagnostic in result.CompilationDiagnosticKinds)
            {
                compilationDiagnostics.Add(diagnostic);
            }
        }

        foreach (var diagnosticKind in workspaceDiagnostics.Distinct(StringComparer.Ordinal))
        {
            allIssues.Add(new AnalysisIssue(
                allProjects.FirstOrDefault()?.Key ?? "workspace",
                "KMLOGP004",
                "error",
                "MSBuild reported a workspace failure while loading the project graph.",
                string.Empty,
                Array.Empty<SourceLocation>()));
        }

        if (allEvents.Count == 0)
        {
            allIssues.Add(new AnalysisIssue(
                allProjects.FirstOrDefault()?.Key ?? "workspace",
                EmptyEventsIssueCode,
                "error",
                EmptyEventsIssueMessage,
                string.Empty,
                Array.Empty<SourceLocation>()));
        }

        return new ManifestDocument(
            1,
            allProjects,
            allEvents,
            allUnsupported,
            allIssues,
            compilationDiagnostics.Order(StringComparer.Ordinal).ToArray(),
            workspaceDiagnostics).Canonicalize();
    }

    private sealed class ProjectExtractor
    {
        private readonly Project project;
        private readonly Compilation compilation;
        private readonly ProjectIdentity projectIdentity;
        private readonly List<EventContract> events = [];
        private readonly List<UnsupportedDeclaration> unsupported = [];
        private readonly List<AnalysisIssue> analysisIssues = [];
        private readonly List<SourceLocation> sourceOccurrences = [];
        private readonly HashSet<SyntaxTree> scannedTrees = [];
        private readonly Dictionary<string, List<SourceLocation>> sourceDeclarations = new(StringComparer.Ordinal);
        private readonly HashSet<string> pairedGeneratedDeclarations = new(StringComparer.Ordinal);
        private readonly string projectDirectory;

        internal ProjectExtractor(Project project, Compilation compilation, ProjectIdentity projectIdentity)
        {
            this.project = project;
            this.compilation = compilation;
            this.projectIdentity = projectIdentity;
            projectDirectory = Path.GetDirectoryName(Path.GetFullPath(project.FilePath ?? throw new ProjectAnalysisException("Project path is required.")))!;
        }

        internal async Task<ProjectExtractionResult> ExtractAsync(CancellationToken cancellationToken)
        {
            foreach (var document in project.Documents.OrderBy(document => document.Name, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var root = await document.GetSyntaxRootAsync(cancellationToken);
                var model = await document.GetSemanticModelAsync(cancellationToken);
                if (root is not null && model is not null)
                {
                    scannedTrees.Add(root.SyntaxTree);
                    Scan(root, model, document.FilePath ?? document.Name, true);
                }
            }

            foreach (var tree in compilation.SyntaxTrees.OrderBy(tree => NormalizeGeneratedPath(tree.FilePath), StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!scannedTrees.Add(tree))
                {
                    continue;
                }
                Scan(tree.GetRoot(cancellationToken), compilation.GetSemanticModel(tree), tree.FilePath ?? "generated.cs", false);
            }

            foreach (var pair in sourceDeclarations.Where(pair => pair.Value.Count > 1).OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                analysisIssues.Add(new AnalysisIssue(
                    projectIdentity.Key,
                    "KMLOGP001",
                    "error",
                    "Multiple project-source LoggerMessage declarations share one stable method identity; generated pairing is ambiguous.",
                    pair.Key,
                    pair.Value.OrderBy(source => source.File, StringComparer.Ordinal).ThenBy(source => source.Line).ToArray()));
            }

            var diagnostics = compilation.GetDiagnostics(cancellationToken)
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error || diagnostic.Severity == DiagnosticSeverity.Warning)
                .Select(diagnostic => diagnostic.Id + ":" + diagnostic.Severity)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            var fatalDiagnostics = diagnostics.Where(value => value.EndsWith(":Error", StringComparison.Ordinal) && !value.StartsWith("CS8795:", StringComparison.Ordinal)).ToArray();
            if (fatalDiagnostics.Length > 0)
            {
                analysisIssues.Add(new AnalysisIssue(
                    projectIdentity.Key,
                    "KMLOGP005",
                    "error",
                    "The project compilation contains errors other than the design-time source-generator partial-method diagnostic; the extracted contract is not trustworthy.",
                    string.Empty,
                    Array.Empty<SourceLocation>()));
            }

            return new ProjectExtractionResult(events, unsupported, analysisIssues, diagnostics);
        }

        private void Scan(SyntaxNode root, SemanticModel model, string filePath, bool isProjectSource)
        {
            foreach (var methodSyntax in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var methodSymbol = model.GetDeclaredSymbol(methodSyntax);
                var attribute = methodSymbol?.GetAttributes().FirstOrDefault(attribute => string.Equals(attribute.AttributeClass?.ToDisplayString(), LoggerMessageAttributeName, StringComparison.Ordinal));
                if (methodSymbol is null || attribute is null)
                {
                    continue;
                }

                var source = new SourceLocation(isProjectSource ? NormalizeProjectRelativePath(filePath) : NormalizeGeneratedPath(filePath), methodSyntax.GetLocation().GetLineSpan().StartLinePosition.Line + 1, isProjectSource ? "source" : "generated");
                sourceOccurrences.Add(source);
                var declarationKey = GetDeclarationKey(methodSymbol);
                if (isProjectSource)
                {
                    if (!sourceDeclarations.TryGetValue(declarationKey, out var declarations))
                    {
                        declarations = [];
                        sourceDeclarations.Add(declarationKey, declarations);
                    }
                    declarations.Add(source);
                }
                else if (IsGeneratedImplementation(methodSyntax))
                {
                    if (sourceDeclarations.TryGetValue(declarationKey, out var declarations) && declarations.Count == 1 && pairedGeneratedDeclarations.Add(declarationKey))
                    {
                        continue;
                    }

                    AddUnsupported(source, methodSyntax, declarationKey, "generated LoggerMessage implementation is not paired one-to-one with a project-source declaration");
                    continue;
                }
                else
                {
                    AddUnsupported(source, methodSyntax, declarationKey, "generated LoggerMessage declaration has no project-source counterpart");
                    analysisIssues.Add(new AnalysisIssue(projectIdentity.Key, "KMLOGP002", "warning", "A generated LoggerMessage declaration has no project-source counterpart and was reported explicitly.", declarationKey, [source]));
                    continue;
                }

                if (!TryExtract(methodSyntax, methodSymbol, attribute, source, out var contract, out var reason))
                {
                    AddUnsupported(source, methodSyntax, declarationKey, reason!);
                    continue;
                }

                events.Add(contract!);
            }
        }

        private void AddUnsupported(SourceLocation source, MethodDeclarationSyntax syntax, string declarationKey, string reason) => unsupported.Add(new UnsupportedDeclaration(projectIdentity.Key, source, NormalizeDeclaration(syntax.ToString()), declarationKey, reason));

        private string NormalizeProjectRelativePath(string path)
        {
            var candidate = Path.IsPathRooted(path) ? Path.GetRelativePath(projectDirectory, path) : path;
            return NormalizePath(candidate);
        }

        private static string NormalizeGeneratedPath(string? path) => "generated/" + (string.IsNullOrWhiteSpace(path) ? "generated.cs" : Path.GetFileName(path!));

        private static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('.', '/');
    }

    private sealed record ProjectExtractionResult(
        IReadOnlyList<EventContract> Events,
        IReadOnlyList<UnsupportedDeclaration> Unsupported,
        IReadOnlyList<AnalysisIssue> AnalysisIssues,
        IReadOnlyList<string> CompilationDiagnosticKinds);

    private static bool TryExtract(MethodDeclarationSyntax syntax, IMethodSymbol method, AttributeData attribute, SourceLocation source, out EventContract? contract, out string? reason)
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
            reason = "generic logging methods are outside the supported LoggerMessage scope";
            return false;
        }
        if (!method.Parameters.Any(parameter => IsLogger(parameter.Type)))
        {
            reason = "method has no ILogger parameter";
            return false;
        }
        if (!TryReadAttribute(attribute, method, out var eventId, out var eventName, out var level, out var message, out reason))
        {
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
        contract = new EventContract(
            "",
            GetDeclarationKey(method).Replace("global::", string.Empty, StringComparison.Ordinal),
            containingTypeName,
            method.Name,
            method.Arity,
            method.Parameters.Select(parameter => parameter.RefKind.ToString()).ToArray(),
            eventId,
            eventName,
            level,
            message,
            placeholders,
            method.Parameters.Select(parameter => GetParameterForm(parameter.Type)).ToArray(),
            source);
        return true;
    }

    private static bool TryReadAttribute(AttributeData attribute, IMethodSymbol method, out int eventId, out string eventName, out string level, out string message, out string? reason)
    {
        eventId = 0;
        eventName = method.Name;
        level = string.Empty;
        message = string.Empty;
        reason = null;
        int? suppliedEventId = null;
        string? suppliedEventName = null;
        string? suppliedLevel = null;

        foreach (var argument in attribute.ConstructorArguments)
        {
            if (argument.Kind == TypedConstantKind.Error)
            {
                reason = "attribute arguments are not compile-time constants in the supported LoggerMessage shape";
                return false;
            }

            if (argument.Type?.Name == "LogLevel") suppliedLevel = EnumName(argument);
            else if (argument.Type?.SpecialType == SpecialType.System_Int32) suppliedEventId = Convert.ToInt32(argument.Value, CultureInfo.InvariantCulture);
            else if (argument.Type?.SpecialType == SpecialType.System_String) message = argument.Value as string ?? string.Empty;
        }

        foreach (var (name, argument) in attribute.NamedArguments)
        {
            if (argument.Kind == TypedConstantKind.Error)
            {
                reason = "attribute arguments are not compile-time constants in the supported LoggerMessage shape";
                return false;
            }

            switch (name)
            {
                case "EventId" when argument.Type?.SpecialType == SpecialType.System_Int32: suppliedEventId = Convert.ToInt32(argument.Value, CultureInfo.InvariantCulture); break;
                case "EventName" when argument.Type?.SpecialType == SpecialType.System_String: suppliedEventName = argument.Value as string; break;
                case "Level" when argument.Type?.Name == "LogLevel": suppliedLevel = EnumName(argument); break;
                case "Message" when argument.Type?.SpecialType == SpecialType.System_String: message = argument.Value as string ?? string.Empty; break;
            }
        }

        if (string.IsNullOrEmpty(message))
        {
            reason = "attribute arguments are not compile-time constants in the supported LoggerMessage shape";
            return false;
        }

        eventName = string.IsNullOrWhiteSpace(suppliedEventName) ? method.Name : suppliedEventName;
        eventId = suppliedEventId ?? GetNonRandomizedHashCode(eventName);
        if (suppliedLevel is not null)
        {
            level = suppliedLevel;
        }
        else
        {
            if (!method.Parameters.Any(parameter => IsLogLevel(parameter.Type)))
            {
                reason = "level was omitted and no LogLevel parameter supplies a dynamic level";
                return false;
            }

            level = DynamicLevelName;
        }

        return true;
    }

    private static bool TryReadPlaceholders(string message, out IReadOnlyList<Placeholder> placeholders, out string? reason)
    {
        var values = new List<Placeholder>();
        reason = null;
        for (var index = 0; index < message.Length; index++)
        {
            if (message[index] == '{' && index + 1 < message.Length && message[index + 1] == '{') { index++; continue; }
            if (message[index] == '}' && index + 1 < message.Length && message[index + 1] == '}') { index++; continue; }
            if (message[index] != '{') continue;
            var close = message.IndexOf('}', index + 1);
            if (close < 0) { placeholders = []; reason = "message template contains an unterminated placeholder"; return false; }
            var token = message[(index + 1)..close];
            if (token.Contains('{', StringComparison.Ordinal)) { placeholders = []; reason = "message template contains a nested placeholder"; return false; }
            var separator = token.IndexOfAny([',', ':']);
            var name = (separator < 0 ? token : token[..separator]).Trim();
            if (name.Length == 0) { placeholders = []; reason = "message template contains an empty placeholder"; return false; }
            values.Add(new Placeholder(name, token));
            index = close;
        }
        placeholders = values;
        return true;
    }

    private static string GetDeclarationKey(IMethodSymbol method) => method.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "." + method.Name + "`" + method.Arity.ToString(CultureInfo.InvariantCulture) + "(" + string.Join(",", method.Parameters.Select(parameter => $"{parameter.RefKind}:{GetParameterForm(parameter.Type)}:{parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}")) + ")";

    private static string EnumName(TypedConstant argument) => Convert.ToInt32(argument.Value, CultureInfo.InvariantCulture) switch
    {
        0 => "Trace",
        1 => "Debug",
        2 => "Information",
        3 => "Warning",
        4 => "Error",
        5 => "Critical",
        6 => "None",
        var value => value.ToString(CultureInfo.InvariantCulture)
    };

    private static int GetNonRandomizedHashCode(string value)
    {
        uint result = 2166136261u;
        foreach (var character in value)
        {
            result = (character ^ result) * 16777619;
        }

        var hash = (int)result;
        return hash == int.MinValue ? 0 : Math.Abs(hash);
    }

    private static bool IsLogger(ITypeSymbol type) => type.Name == "ILogger" && type.ContainingNamespace.ToDisplayString() == "Microsoft.Extensions.Logging";
    private static bool IsLogLevel(ITypeSymbol type) => type.Name == "LogLevel" && type.ContainingNamespace.ToDisplayString() == "Microsoft.Extensions.Logging";
    private static string GetParameterForm(ITypeSymbol type) => ManifestJson.GetRequiredParameterForm(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty, StringComparison.Ordinal));
    private static bool IsException(ITypeSymbol type)
    {
        for (var current = type; current is not null; current = (current as INamedTypeSymbol)?.BaseType)
        {
            if (current.Name == "Exception" && current.ContainingNamespace.ToDisplayString() == "System") return true;
        }
        return false;
    }

    private static bool IsGeneratedImplementation(MethodDeclarationSyntax syntax) => syntax.AttributeLists.SelectMany(attributes => attributes.Attributes).Any(attribute => attribute.Name.ToString().EndsWith("GeneratedCode", StringComparison.Ordinal) || attribute.Name.ToString().EndsWith("GeneratedCodeAttribute", StringComparison.Ordinal));
    private static string NormalizeDeclaration(string declaration) => declaration.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}

internal sealed class ProjectAnalysisException(string message) : Exception(message);
