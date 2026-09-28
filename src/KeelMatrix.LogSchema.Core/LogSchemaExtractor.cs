using System.Globalization;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
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

        var budget = new ProjectAnalysisBudget();
        await ProjectAnalysisPreflight.ValidateAsync(fullPath, budget, cancellationToken);

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

        budget.ObserveProjectCount(projects.Length);

        foreach (var project in projects)
        {
            budget.BeginProject();
            foreach (var document in project.Documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceBytes = await GetSourceDocumentBytesAsync(document, cancellationToken);
                budget.ObserveSourceDocument(sourceBytes);
            }
        }

        var allProjects = new List<ProjectIdentity>();
        var allEvents = new List<EventContract>();
        var allUnsupported = new List<UnsupportedDeclaration>();
        var allIssues = new List<AnalysisIssue>();
        var compilationDiagnostics = new HashSet<string>(StringComparer.Ordinal);
        var generatorVersionsByProject = new Dictionary<string, string>(StringComparer.Ordinal);

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
            var analyzer = new ProjectExtractor(project, compilation, identity, budget);
            var result = await analyzer.ExtractAsync(cancellationToken);
            allEvents.AddRange(result.Events.Select(@event => @event with { ProjectKey = projectKey }));
            allUnsupported.AddRange(result.Unsupported.Select(item => item with { ProjectKey = projectKey }));
            allIssues.AddRange(result.AnalysisIssues.Select(issue => issue with { ProjectKey = projectKey }));
            if (result.GeneratorVersion is not null)
            {
                generatorVersionsByProject[projectKey] = result.GeneratorVersion;
            }
            foreach (var diagnostic in result.CompilationDiagnosticKinds)
            {
                compilationDiagnostics.Add(diagnostic);
            }
        }

        var solutionGeneratorVersions = generatorVersionsByProject.Values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (solutionGeneratorVersions.Length > 1)
        {
            var firstProject = allProjects[0].Key;
            var projectVersions = string.Join(", ", generatorVersionsByProject.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"));
            allIssues.Add(new AnalysisIssue(
                firstProject,
                LoggerMessageGeneratorSemantics.MixedGeneratorVersionIssueCode,
                "error",
                $"The solution resolves mixed Microsoft.Extensions.Logging.Generators assembly versions ({projectVersions}); capture fails closed at solution scope.",
                string.Empty,
                Array.Empty<SourceLocation>()));
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

    private static async Task<long> GetSourceDocumentBytesAsync(Document document, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(document.FilePath))
        {
            try
            {
                var fileInfo = new FileInfo(document.FilePath);
                if (fileInfo.Exists)
                {
                    return fileInfo.Length;
                }
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Fall back to the workspace text. The workspace still owns
                // the source identity and can provide a bounded text object.
            }
        }

        var text = await document.GetTextAsync(cancellationToken);
        if (text.Length > ProjectAnalysisLimits.MaxSourceBytesPerDocument)
        {
            throw new ProjectAnalysisException(ProjectAnalysisLimits.SourceDocumentBytesMessage(text.Length));
        }

        return Encoding.UTF8.GetByteCount(text.ToString());
    }

    private static class ProjectAnalysisPreflight
    {
        private static readonly Regex SolutionProjectLine = new(
            "^Project\\(\\\"[^\\\"]+\\\"\\)\\s*=\\s*\\\"[^\\\"]*\\\",\\s*\\\"(?<path>[^\\\"]+)\\\"",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        internal static async Task ValidateAsync(string inputPath, ProjectAnalysisBudget budget, CancellationToken cancellationToken)
        {
            var extension = Path.GetExtension(inputPath);
            if (extension.Equals(".sln", StringComparison.OrdinalIgnoreCase))
            {
                var fileLength = new FileInfo(inputPath).Length;
                if (fileLength > ProjectAnalysisLimits.MaxSolutionFileBytes)
                {
                    throw new ProjectAnalysisException(ProjectAnalysisLimits.SolutionFileBytesMessage(fileLength));
                }

                var projectCount = await CountSlnProjectsAsync(inputPath, cancellationToken);
                if (projectCount > 0)
                {
                    budget.ObserveProjectCount(projectCount);
                }
            }
            else if (extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                var fileLength = new FileInfo(inputPath).Length;
                if (fileLength > ProjectAnalysisLimits.MaxSolutionFileBytes)
                {
                    throw new ProjectAnalysisException(ProjectAnalysisLimits.SolutionFileBytesMessage(fileLength));
                }

                var projectCount = await CountSlnxProjectsAsync(inputPath, cancellationToken);
                if (projectCount > 0)
                {
                    budget.ObserveProjectCount(projectCount);
                }
            }
            else
            {
                budget.ObserveProjectCount(1);
            }
        }

        private static async Task<int> CountSlnProjectsAsync(string path, CancellationToken cancellationToken)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var count = 0;
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var match = SolutionProjectLine.Match(line);
                if (!match.Success || !match.Groups["path"].Value.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                count++;
                if (count > ProjectAnalysisLimits.MaxProjects)
                {
                    throw new ProjectAnalysisException(ProjectAnalysisLimits.ProjectCountMessage(count));
                }
            }

            return count;
        }

        private static async Task<int> CountSlnxProjectsAsync(string path, CancellationToken cancellationToken)
        {
            var settings = new XmlReaderSettings
            {
                Async = true,
                DtdProcessing = DtdProcessing.Prohibit,
                MaxCharactersInDocument = ProjectAnalysisLimits.MaxSolutionFileBytes
            };
            using var reader = XmlReader.Create(path, settings);
            var count = 0;
            while (await reader.ReadAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.NodeType != XmlNodeType.Element || !reader.LocalName.Equals("Project", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var projectPath = reader.GetAttribute("Path");
                if (projectPath is null || !projectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                count++;
                if (count > ProjectAnalysisLimits.MaxProjects)
                {
                    throw new ProjectAnalysisException(ProjectAnalysisLimits.ProjectCountMessage(count));
                }
            }

            return count;
        }
    }

    private sealed class ProjectExtractor
    {
        private readonly Project project;
        private readonly Compilation compilation;
        private readonly ProjectIdentity projectIdentity;
        private readonly List<EventContract> events = [];
        private readonly List<UnsupportedDeclaration> unsupported = [];
        private readonly List<AnalysisIssue> analysisIssues = [];
        private readonly HashSet<SyntaxTree> scannedTrees = [];
        private readonly Dictionary<string, List<SourceLocation>> sourceDeclarations = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<SourceMethod>> sourceMethods = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<SourceCandidate>> sourceCandidates = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> generatedImplementationCounts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> remainingPinnedCompiledImplementations = new(StringComparer.Ordinal);
        private readonly HashSet<string> pairedGeneratedDeclarations = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> generatorDiagnosticDeclarations = new(StringComparer.Ordinal);
        private readonly HashSet<string> generatorDiagnosticKinds = new(StringComparer.Ordinal);
        private readonly SourceProvenanceRegistry sourceProvenance = new();
        private readonly HashSet<string> reportedProvenanceCollisions = new(StringComparer.Ordinal);
        private readonly string projectDirectory;
        private readonly ProjectAnalysisBudget budget;
        private bool hasLoggerMessageDeclaration;

        internal ProjectExtractor(Project project, Compilation compilation, ProjectIdentity projectIdentity, ProjectAnalysisBudget budget)
        {
            this.project = project;
            this.compilation = compilation;
            this.projectIdentity = projectIdentity;
            this.budget = budget;
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
                    Scan(root, model, document.FilePath ?? document.Name, true, root.SyntaxTree);
                }
            }

            var projectSourceTrees = scannedTrees.ToArray();
            var syntaxTrees = compilation.SyntaxTrees.ToArray();
            budget.ObserveSyntaxTrees(syntaxTrees.Length);
            var compiledGeneratedTrees = new List<GeneratedSyntaxTree>();
            foreach (var tree in syntaxTrees)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!scannedTrees.Add(tree))
                {
                    continue;
                }

                budget.ObserveGeneratedSyntaxTree();
                var text = tree.GetText(cancellationToken);
                if (text.Length > ProjectAnalysisLimits.MaxGeneratedSourceBytesPerTree)
                {
                    throw new ProjectAnalysisException(ProjectAnalysisLimits.GeneratedTreeBytesMessage(text.Length));
                }

                var generatedText = text.ToString();
                var generatedBytes = (text.Encoding ?? Encoding.UTF8).GetByteCount(generatedText);
                budget.ObserveGeneratedSourceBytes(generatedBytes);
                var logicalPath = SourceProvenance.NormalizeGeneratedPath(tree.FilePath, generatedText);
                compiledGeneratedTrees.Add(new GeneratedSyntaxTree(tree, logicalPath, generatedText));
            }

            var generatorRun = RunPinnedGenerator(projectSourceTrees, cancellationToken);
            foreach (var diagnostic in generatorRun.Diagnostics)
            {
                AddGeneratorDiagnostic(diagnostic);
            }

            foreach (var generatedTree in generatorRun.GeneratedTrees.OrderBy(tree => tree.LogicalPath, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                budget.ObserveGeneratedSyntaxTree();
                var generatedBytes = (generatedTree.Tree.GetText(cancellationToken).Encoding ?? Encoding.UTF8).GetByteCount(generatedTree.Text);
                budget.ObserveGeneratedSourceBytes(generatedBytes);
                Scan(
                    generatedTree.Tree.GetRoot(cancellationToken),
                    generatorRun.OutputCompilation.GetSemanticModel(generatedTree.Tree),
                    generatedTree.Tree.FilePath,
                    false,
                    generatedTree.Tree,
                    generatedTree.LogicalPath,
                    generatedTree.Text,
                    generatorRun.GeneratorVersion,
                    true);
            }

            foreach (var generatedTree in generatorRun.GeneratedTrees)
            {
                var model = generatorRun.OutputCompilation.GetSemanticModel(generatedTree.Tree);
                foreach (var methodSyntax in generatedTree.Tree.GetRoot(cancellationToken).DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    var methodSymbol = model.GetDeclaredSymbol(methodSyntax, cancellationToken);
                    var attribute = methodSymbol?.GetAttributes().FirstOrDefault(attribute => string.Equals(attribute.AttributeClass?.ToDisplayString(), LoggerMessageAttributeName, StringComparison.Ordinal));
                    if (methodSymbol is not null && attribute is not null && generatorRun.GeneratorVersion is not null && IsPinnedGeneratedImplementation(methodSymbol, model.Compilation, generatorRun.GeneratorVersion))
                    {
                        var declarationKey = GetDeclarationKey(methodSymbol);
                        remainingPinnedCompiledImplementations[declarationKey] = remainingPinnedCompiledImplementations.GetValueOrDefault(declarationKey) + 1;
                    }
                }
            }

            foreach (var generatedTree in compiledGeneratedTrees.OrderBy(tree => tree.LogicalPath, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Scan(
                    generatedTree.Tree.GetRoot(cancellationToken),
                    compilation.GetSemanticModel(generatedTree.Tree),
                    generatedTree.Tree.FilePath,
                    false,
                    generatedTree.Tree,
                    generatedTree.LogicalPath,
                    generatedTree.Text,
                    generatorRun.GeneratorVersion,
                    false);
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

            foreach (var pair in sourceCandidates.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                var generatedCount = generatedImplementationCounts.GetValueOrDefault(pair.Key);
                var hasExactlyOneSource = sourceDeclarations.TryGetValue(pair.Key, out var declarations) && declarations.Count == 1;
                if (hasExactlyOneSource && generatedCount == 1 && !generatorDiagnosticDeclarations.ContainsKey(pair.Key))
                {
                    events.AddRange(pair.Value.Select(candidate => candidate.Contract));
                    continue;
                }

                var reason = generatorDiagnosticDeclarations.TryGetValue(pair.Key, out var generatorDiagnostic)
                    ? generatorDiagnostic
                    : generatedCount == 0
                    ? "source LoggerMessage declaration has no generated implementation counterpart"
                    : $"source LoggerMessage declaration has {generatedCount.ToString(CultureInfo.InvariantCulture)} generated implementation counterparts; exactly one is required";
                foreach (var candidate in pair.Value)
                {
                    AddUnsupported(candidate.Source, candidate.Declaration, pair.Key, reason);
                }
            }

            var versionPairRejected = !generatorRun.IsSupportedPair &&
                (hasLoggerMessageDeclaration || generatorRun.AbstractionsVersion is not null || generatorRun.GeneratorVersion is not null);
            if (versionPairRejected)
            {
                analysisIssues.Add(new AnalysisIssue(
                    projectIdentity.Key,
                    LoggerMessageGeneratorSemantics.GeneratorVersionIssueCode,
                    "error",
                    LoggerMessageGeneratorSemantics.GeneratorVersionFailureMessage(generatorRun.AbstractionsVersion, generatorRun.GeneratorVersion),
                    string.Empty,
                    Array.Empty<SourceLocation>()));
            }

            var diagnostics = compilation.GetDiagnostics(cancellationToken)
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error || diagnostic.Severity == DiagnosticSeverity.Warning)
                .Select(diagnostic => diagnostic.Id + ":" + diagnostic.Severity)
                .Concat(generatorDiagnosticKinds)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            // SYSLIB10xx is the pinned LoggerMessage generator's declaration-diagnostic family. Those
            // diagnostics are consumed from GeneratorDriver.GetRunResult above and their declarations remain
            // unsupported; the compiler-side consequence must not hide the more precise paired outcome.
            var fatalDiagnostics = diagnostics.Where(value => value.EndsWith(":Error", StringComparison.Ordinal) &&
                !value.StartsWith("CS8795:", StringComparison.Ordinal) &&
                !value.StartsWith("SYSLIB10", StringComparison.Ordinal)).ToArray();
            // A cross-pair can make the compiler report follow-on errors from the
            // generator/reference mismatch. KMLOGP009 is the precise fail-closed
            // result for that bounded family; do not hide it behind KMLOGP005.
            if (fatalDiagnostics.Length > 0 && !versionPairRejected)
            {
                analysisIssues.Add(new AnalysisIssue(
                    projectIdentity.Key,
                    "KMLOGP005",
                    "error",
                    "The project compilation contains errors other than the design-time source-generator partial-method diagnostic; the extracted contract is not trustworthy.",
                    string.Empty,
                    Array.Empty<SourceLocation>()));
            }

            return new ProjectExtractionResult(events, unsupported, analysisIssues, diagnostics, generatorRun.GeneratorVersion);
        }

        private void Scan(
            SyntaxNode root,
            SemanticModel model,
            string? filePath,
            bool isProjectSource,
            SyntaxTree syntaxTree,
            string? generatedLogicalPath = null,
            string? generatedText = null,
            string? generatedVersion = null,
            bool isPinnedGenerated = false)
        {
            var logicalPath = isProjectSource
                ? SourceProvenance.NormalizeProjectRelativePath(projectDirectory, filePath ?? string.Empty)
                : generatedLogicalPath ?? SourceProvenance.NormalizeGeneratedPath(filePath, generatedText);
            if (!sourceProvenance.TryRegister(isProjectSource ? "source" : "generated", logicalPath, syntaxTree) && reportedProvenanceCollisions.Add(logicalPath))
            {
                analysisIssues.Add(new AnalysisIssue(
                    projectIdentity.Key,
                    "KMLOGP008",
                    "error",
                    "Distinct syntax trees share one canonical source provenance identity.",
                    logicalPath,
                    [new SourceLocation(logicalPath, 1, isProjectSource ? "source" : "generated")]));
            }

            foreach (var methodSyntax in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var methodSymbol = model.GetDeclaredSymbol(methodSyntax);
                var attribute = methodSymbol?.GetAttributes().FirstOrDefault(attribute => string.Equals(attribute.AttributeClass?.ToDisplayString(), LoggerMessageAttributeName, StringComparison.Ordinal));
                if (methodSymbol is null || attribute is null)
                {
                    continue;
                }

                var declarationKey = GetDeclarationKey(methodSymbol);
                if (!isProjectSource && !isPinnedGenerated && generatedVersion is not null && IsPinnedGeneratedImplementation(methodSymbol, model.Compilation, generatedVersion) && remainingPinnedCompiledImplementations.GetValueOrDefault(declarationKey) > 0)
                {
                    // The compiler compilation contains the pinned generator output a second time. It
                    // was already observed from GeneratorDriver.GetRunResult, so do not charge the
                    // same declaration against the project-analysis budget again.
                    remainingPinnedCompiledImplementations[declarationKey]--;
                    continue;
                }

                budget.ObserveLoggerMessageDeclaration();
                hasLoggerMessageDeclaration |= isProjectSource;

                var source = new SourceLocation(logicalPath, methodSyntax.GetLocation().GetLineSpan().StartLinePosition.Line + 1, isProjectSource ? "source" : "generated");
                if (isProjectSource)
                {
                    if (!sourceDeclarations.TryGetValue(declarationKey, out var declarations))
                    {
                        declarations = [];
                        sourceDeclarations.Add(declarationKey, declarations);
                    }
                    declarations.Add(source);
                }
                if (isProjectSource)
                {
                    if (!sourceMethods.TryGetValue(declarationKey, out var methods))
                    {
                        methods = [];
                        sourceMethods.Add(declarationKey, methods);
                    }
                    methods.Add(new SourceMethod(methodSyntax, methodSymbol, source));
                }
                else if (isPinnedGenerated && generatedVersion is not null && IsPinnedGeneratedImplementation(methodSymbol, model.Compilation, generatedVersion))
                {
                    generatedImplementationCounts[declarationKey] = generatedImplementationCounts.GetValueOrDefault(declarationKey) + 1;
                    if (sourceDeclarations.TryGetValue(declarationKey, out var declarations) && declarations.Count == 1 && pairedGeneratedDeclarations.Add(declarationKey))
                    {
                        continue;
                    }

                    AddUnsupported(source, methodSyntax, declarationKey, "generated LoggerMessage implementation is not paired one-to-one with a project-source declaration");
                    continue;
                }
                else
                {
                    var generatedReason = isPinnedGenerated
                        ? "pinned generator output method is missing the verified System.CodeDom.Compiler.GeneratedCodeAttribute tool/version pair"
                        : "generated LoggerMessage declaration was not produced by the resolved Microsoft.Extensions.Logging.Generators assembly";
                    AddUnsupported(source, methodSyntax, declarationKey, generatedReason);
                    analysisIssues.Add(new AnalysisIssue(projectIdentity.Key, "KMLOGP002", "warning", "A generated LoggerMessage declaration has no project-source counterpart and was reported explicitly.", declarationKey, [source]));
                    continue;
                }

                if (!TryExtract(methodSyntax, methodSymbol, attribute, compilation, source, out var contract, out var extractReason))
                {
                    AddUnsupported(source, methodSyntax, declarationKey, extractReason!);
                    continue;
                }

                budget.ObserveEvent();
                if (!sourceCandidates.TryGetValue(declarationKey, out var candidates))
                {
                    candidates = [];
                    sourceCandidates.Add(declarationKey, candidates);
                }
                candidates.Add(new SourceCandidate(source, NormalizeDeclaration(methodSyntax.ToString()), contract!));
            }
        }

        private void AddUnsupported(SourceLocation source, MethodDeclarationSyntax syntax, string declarationKey, string reason)
        {
            AddUnsupported(source, NormalizeDeclaration(syntax.ToString()), declarationKey, reason);
        }

        private void AddUnsupported(SourceLocation source, string declaration, string declarationKey, string reason)
        {
            budget.ObserveUnsupportedDeclaration();
            unsupported.Add(new UnsupportedDeclaration(projectIdentity.Key, source, declaration, declarationKey, reason));
        }

        private GeneratorRunResult RunPinnedGenerator(IReadOnlyCollection<SyntaxTree> projectSourceTrees, CancellationToken cancellationToken)
        {
            var generatorReferences = project.AnalyzerReferences
                .OfType<AnalyzerFileReference>()
                .Where(reference => string.Equals(Path.GetFileName(reference.FullPath), LoggerMessageGeneratorSemantics.GeneratorAssemblyName + ".dll", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (generatorReferences.Length != 1 || generatorReferences[0] is not AnalyzerFileReference analyzerReference)
            {
                return GeneratorRunResult.Failed(compilation, null, ResolveAbstractionsVersion());
            }

            var generatorVersion = GetAssemblyFileVersion(analyzerReference.FullPath);
            if (generatorVersion is null)
            {
                return GeneratorRunResult.Failed(compilation, null, ResolveAbstractionsVersion());
            }

            IReadOnlyList<ISourceGenerator> generators;
            try
            {
                generators = analyzerReference.GetGenerators(LanguageNames.CSharp);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or BadImageFormatException or FileNotFoundException or FileLoadException)
            {
                return GeneratorRunResult.Failed(compilation, generatorVersion, ResolveAbstractionsVersion());
            }

            if (generators.Count != 1)
            {
                return GeneratorRunResult.Failed(compilation, generatorVersion, ResolveAbstractionsVersion());
            }

            var sourceTrees = projectSourceTrees.ToArray();
            var inputCompilation = compilation.RemoveSyntaxTrees(compilation.SyntaxTrees.Where(tree => !sourceTrees.Contains(tree)).ToArray());
            GeneratorDriver driver = CSharpGeneratorDriver.Create(
                generators,
                parseOptions: sourceTrees.FirstOrDefault()?.Options as CSharpParseOptions,
                optionsProvider: null);
            driver = driver.RunGeneratorsAndUpdateCompilation(inputCompilation, out var outputCompilation, out _, cancellationToken);
            var runResult = driver.GetRunResult();
            var generatedTrees = runResult.GeneratedTrees
                .Select(tree =>
                {
                    var text = tree.GetText(cancellationToken).ToString();
                    var logicalPath = SourceProvenance.NormalizeGeneratedPath(tree.FilePath, text);
                    return new GeneratedSyntaxTree(tree, logicalPath, text);
                })
                .ToArray();
            var resolvedAbstractionsVersion = ResolveAbstractionsVersion();
            return new GeneratorRunResult(
                outputCompilation,
                generatedTrees,
                runResult.Diagnostics,
                generatorVersion,
                resolvedAbstractionsVersion,
                LoggerMessageGeneratorSemantics.IsSupportedVersionPair(resolvedAbstractionsVersion, generatorVersion));
        }

        private string? ResolveAbstractionsVersion()
        {
            var versions = compilation.References
                .OfType<PortableExecutableReference>()
                .Where(reference => string.Equals(Path.GetFileName(reference.FilePath), "Microsoft.Extensions.Logging.Abstractions.dll", StringComparison.OrdinalIgnoreCase))
                .Select(reference => TryResolveAssemblyPackageVersion(reference.FilePath))
                .ToArray();
            return versions.Length == 1 ? versions[0] : null;
        }

        private void AddGeneratorDiagnostic(Diagnostic diagnostic)
        {
            if (diagnostic.Severity is not (DiagnosticSeverity.Error or DiagnosticSeverity.Warning))
            {
                return;
            }

            var diagnosticKind = diagnostic.Id + ":" + diagnostic.Severity;
            generatorDiagnosticKinds.Add(diagnosticKind);
            if (diagnostic.Id == "SYSLIB1015")
            {
                return;
            }

            var sourceMethod = sourceMethods
                .SelectMany(pair => pair.Value.Select(method => (DeclarationKey: pair.Key, Method: method)))
                .FirstOrDefault(candidate => diagnostic.Location.IsInSource &&
                    (candidate.Method.Syntax.SyntaxTree == diagnostic.Location.SourceTree ||
                        string.Equals(candidate.Method.Syntax.SyntaxTree.FilePath, diagnostic.Location.SourceTree?.FilePath, StringComparison.OrdinalIgnoreCase)) &&
                    candidate.Method.Syntax.Span.IntersectsWith(diagnostic.Location.SourceSpan));
            if (sourceMethod.Method is null)
            {
                analysisIssues.Add(new AnalysisIssue(
                    projectIdentity.Key,
                    LoggerMessageGeneratorSemantics.GeneratorDiagnosticIssueCode,
                    "error",
                    $"Pinned Microsoft.Extensions.Logging.Generators diagnostic {diagnosticKind} could not be paired with one source declaration; capture fails closed.",
                    string.Empty,
                    Array.Empty<SourceLocation>()));
                return;
            }

            generatorDiagnosticDeclarations[sourceMethod.DeclarationKey] = $"source LoggerMessage declaration has pinned generator diagnostic {diagnosticKind}: {diagnostic.GetMessage(CultureInfo.InvariantCulture)}";
        }

        private static bool IsPinnedGeneratedImplementation(IMethodSymbol method, Compilation compilation, string generatorVersion)
        {
            var generatedCodeAttribute = compilation.GetTypeByMetadataName("System.CodeDom.Compiler.GeneratedCodeAttribute");
            var attributes = method.GetAttributes();
            return generatedCodeAttribute is not null && attributes.Any(attribute => LoggerMessageGeneratorSemantics.IsPinnedGeneratedCodeAttribute(attribute, generatedCodeAttribute, generatorVersion));
        }

        private static string? GetAssemblyFileVersion(string path)
        {
            try
            {
                var version = FileVersionInfo.GetVersionInfo(path).FileVersion;
                return string.IsNullOrWhiteSpace(version) ? null : version.Trim();
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or FileNotFoundException or UnauthorizedAccessException or BadImageFormatException)
            {
                return null;
            }
        }

        private static string? TryResolveAssemblyPackageVersion(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var normalized = path.Replace('\\', '/');
            var marker = "/microsoft.extensions.logging.abstractions/";
            var markerIndex = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex >= 0)
            {
                var start = markerIndex + marker.Length;
                var end = normalized.IndexOf('/', start);
                if (end > start)
                {
                    var packageVersion = normalized[start..end];
                    if (Version.TryParse(packageVersion, out _))
                    {
                        return packageVersion;
                    }
                }
            }

            try
            {
                var productVersion = FileVersionInfo.GetVersionInfo(path).ProductVersion;
                if (string.IsNullOrWhiteSpace(productVersion))
                {
                    return null;
                }

                var separator = productVersion.IndexOfAny(['+', '-']);
                var version = separator >= 0 ? productVersion[..separator] : productVersion;
                return Version.TryParse(version, out _) ? version : null;
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or FileNotFoundException or UnauthorizedAccessException or BadImageFormatException)
            {
                return null;
            }
        }

        private sealed record GeneratedSyntaxTree(SyntaxTree Tree, string LogicalPath, string Text);
        private sealed record SourceMethod(MethodDeclarationSyntax Syntax, IMethodSymbol Symbol, SourceLocation Source);
        private sealed record SourceCandidate(SourceLocation Source, string Declaration, EventContract Contract);
        private sealed record GeneratorRunResult(
            Compilation OutputCompilation,
            IReadOnlyList<GeneratedSyntaxTree> GeneratedTrees,
            IReadOnlyList<Diagnostic> Diagnostics,
            string? GeneratorVersion,
            string? AbstractionsVersion,
            bool IsSupportedPair)
        {
            internal static GeneratorRunResult Failed(Compilation compilation, string? generatorVersion, string? abstractionsVersion) => new(compilation, Array.Empty<GeneratedSyntaxTree>(), Array.Empty<Diagnostic>(), generatorVersion, abstractionsVersion, false);
        }

    }

    private sealed record ProjectExtractionResult(
        IReadOnlyList<EventContract> Events,
        IReadOnlyList<UnsupportedDeclaration> Unsupported,
        IReadOnlyList<AnalysisIssue> AnalysisIssues,
        IReadOnlyList<string> CompilationDiagnosticKinds,
        string? GeneratorVersion);

    private static bool TryExtract(MethodDeclarationSyntax syntax, IMethodSymbol method, AttributeData attribute, Compilation compilation, SourceLocation source, out EventContract? contract, out string? reason)
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
        if (method.IsAsync)
        {
            reason = "async logging methods are outside the supported LoggerMessage scope";
            return false;
        }
        foreach (var parameter in method.Parameters)
        {
            if (parameter.RefKind != RefKind.None)
            {
                reason = $"parameter '{parameter.Name}' uses ref kind '{parameter.RefKind}', which the LoggerMessage generator rejects";
                return false;
            }
            if (parameter.IsParams)
            {
                reason = $"parameter '{parameter.Name}' uses params, which the LoggerMessage generator rejects";
                return false;
            }
            if (parameter.Type.IsRefLikeType)
            {
                reason = $"parameter '{parameter.Name}' is a ref struct, which the LoggerMessage generator rejects";
                return false;
            }
        }
        if (!TryReadAttribute(attribute, method, out var eventId, out var eventName, out var level, out var message, out reason))
        {
            return false;
        }
        if (!TryReadPlaceholders(message, out var placeholders, out reason))
        {
            return false;
        }

        if (!LoggerMessageGeneratorSemantics.TryClassify(method, compilation, out var semantics, out reason))
        {
            return false;
        }

        var dynamicLevel = string.Equals(level, DynamicLevelName, StringComparison.Ordinal);
        if (dynamicLevel && semantics.LevelParameter is null)
        {
            reason = "level was omitted and no generator-effective LogLevel parameter supplies a dynamic level";
            return false;
        }

        if (!LoggerMessageGeneratorSemantics.TryValidateTemplate(semantics, dynamicLevel, placeholders, out reason))
        {
            return false;
        }

        var structuredState = method.Parameters
            .Where(parameter => semantics.IsStructuredState(parameter.Name, dynamicLevel, placeholders))
            .Select(parameter =>
            {
                return new StructuredStateProperty(parameter.Name, LoggerMessageGeneratorSemantics.EmittedName(parameter.Name, placeholders));
            })
            .ToArray();

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
            source,
            method.Parameters.Select(parameter => new ParameterContract(
                parameter.Name,
                parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty, StringComparison.Ordinal),
                parameter.RefKind.ToString(),
                semantics.Parameter(parameter.Name).Role)).ToArray(),
            structuredState,
            semantics.LoggerParameter,
            semantics.ExceptionParameter,
            dynamicLevel ? "Dynamic" : "Fixed",
            semantics.LevelParameter);
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
            level = DynamicLevelName;
        }

        return true;
    }

    internal static bool TryReadPlaceholders(string message, out IReadOnlyList<Placeholder> placeholders, out string? reason)
    {
        var values = new List<Placeholder>();
        reason = null;
        var scanIndex = 0;
        while (scanIndex < message.Length)
        {
            var open = FindBraceIndex(message, '{', scanIndex, message.Length);
            if (open == -2)
            {
                placeholders = [];
                reason = "message template contains an unmatched closing brace";
                return false;
            }
            if (open == -1)
            {
                break;
            }

            var close = FindBraceIndex(message, '}', open + 1, message.Length);
            if (close < 0)
            {
                placeholders = [];
                reason = "message template contains an unmatched opening brace";
                return false;
            }

            var formatDelimiter = FindIndexOfAny(message, [',', ':'], open, close);
            var name = message.Substring(open + 1, formatDelimiter - open - 1).Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                placeholders = [];
                reason = "message template contains an empty placeholder";
                return false;
            }

            values.Add(new Placeholder(name, message[(open + 1)..close]));
            scanIndex = close + 1;
        }
        placeholders = values;
        return true;
    }

    private static int FindIndexOfAny(string message, char[] characters, int startIndex, int endIndex)
    {
        var index = message.IndexOfAny(characters, startIndex, endIndex - startIndex);
        return index < 0 ? endIndex : index;
    }

    private static int FindBraceIndex(string message, char searchedBrace, int startIndex, int endIndex)
    {
        const int wrongBraceFound = -2;
        const int noBracesFound = -1;
        var scanIndex = startIndex;
        while (scanIndex < endIndex)
        {
            var current = message[scanIndex];
            if (current is '{' or '}')
            {
                var brace = current;
                var beforeSkip = scanIndex;
                while (current == brace && ++scanIndex < endIndex)
                {
                    current = message[scanIndex];
                }

                var count = scanIndex - beforeSkip;
                if (count % 2 != 0)
                {
                    if (brace == searchedBrace)
                    {
                        return brace == '{' ? scanIndex - 1 : beforeSkip;
                    }

                    return wrongBraceFound;
                }
            }
            else
            {
                scanIndex++;
            }
        }

        return noBracesFound;
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

    private static string GetParameterForm(ITypeSymbol type) => ManifestJson.GetRequiredParameterForm(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty, StringComparison.Ordinal));

    private static string NormalizeDeclaration(string declaration) => declaration.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}

internal sealed class ProjectAnalysisException(string message) : Exception(message);
