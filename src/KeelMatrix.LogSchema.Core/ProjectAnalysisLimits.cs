using Microsoft.CodeAnalysis.Text;

namespace KeelMatrix.LogSchema;

internal static class ProjectAnalysisLimits
{
    internal const int MaxProjects = 64;
    internal const int MaxDocumentsPerProject = 512;
    internal const int MaxDocuments = 4096;

    // The byte ceilings leave the measured 24-project / 384-document fixture
    // far below the boundary while preventing a small document graph from
    // turning into an unbounded source-text allocation.
    internal const long MaxSourceBytesPerDocument = 1L * 1024 * 1024;
    internal const long MaxSourceBytesPerProject = 16L * 1024 * 1024;
    internal const long MaxSourceBytes = 64L * 1024 * 1024;

    // A compilation can contain source and generator-produced trees. The
    // generated-tree ceiling is independent so a small source project cannot
    // hide an excessive generated graph behind the total-tree ceiling.
    internal const int MaxSyntaxTreesPerProject = 8192;
    internal const int MaxGeneratedSyntaxTreesPerProject = 4096;
    internal const long MaxGeneratedSourceBytesPerTree = 4L * 1024 * 1024;
    internal const long MaxGeneratedSourceBytes = 64L * 1024 * 1024;

    // These are counts of discovered LoggerMessage candidates, retained
    // supported events, and explicit unsupported records respectively.
    internal const int MaxLoggerMessageDeclarations = 4096;
    internal const int MaxEvents = 4096;
    internal const int MaxUnsupportedDeclarations = 4096;

    internal const long MaxSolutionFileBytes = 16L * 1024 * 1024;

    internal static string ProjectCountMessage(long actual) =>
        LimitMessage("C# projects", actual, MaxProjects);

    internal static string ProjectDocumentCountMessage(long actual) =>
        LimitMessage("source documents in one project", actual, MaxDocumentsPerProject);

    internal static string TotalDocumentCountMessage(long actual) =>
        LimitMessage("source documents across the input", actual, MaxDocuments);

    internal static string SourceDocumentBytesMessage(long actual) =>
        LimitMessage("source bytes in one document", actual, MaxSourceBytesPerDocument);

    internal static string ProjectSourceBytesMessage(long actual) =>
        LimitMessage("source bytes in one project", actual, MaxSourceBytesPerProject);

    internal static string TotalSourceBytesMessage(long actual) =>
        LimitMessage("source bytes across the input", actual, MaxSourceBytes);

    internal static string SyntaxTreeCountMessage(long actual) =>
        LimitMessage("compilation syntax trees in one project", actual, MaxSyntaxTreesPerProject);

    internal static string GeneratedSyntaxTreeCountMessage(long actual) =>
        LimitMessage("generated syntax trees in one project", actual, MaxGeneratedSyntaxTreesPerProject);

    internal static string GeneratedTreeBytesMessage(long actual) =>
        LimitMessage("generated source bytes in one syntax tree", actual, MaxGeneratedSourceBytesPerTree);

    internal static string TotalGeneratedBytesMessage(long actual) =>
        LimitMessage("generated source bytes across the input", actual, MaxGeneratedSourceBytes);

    internal static string LoggerMessageDeclarationCountMessage(long actual) =>
        LimitMessage("discovered LoggerMessage declarations", actual, MaxLoggerMessageDeclarations);

    internal static string EventCountMessage(long actual) =>
        LimitMessage("supported LoggerMessage events", actual, MaxEvents);

    internal static string UnsupportedCountMessage(long actual) =>
        LimitMessage("unsupported LoggerMessage declarations", actual, MaxUnsupportedDeclarations);

    internal static string SolutionFileBytesMessage(long actual) =>
        LimitMessage("solution file bytes during preflight", actual, MaxSolutionFileBytes);

    private static string LimitMessage(string resource, long actual, long maximum) =>
        $"Project analysis resource limit exceeded: {resource} is {actual}; the maximum supported is {maximum}. Reduce the input graph or source content and retry.";
}

internal sealed class ProjectAnalysisBudget
{
    private int currentProjectSourceDocuments;
    private long currentProjectSourceBytes;
    private int currentGeneratedSyntaxTrees;

    internal long SourceDocuments { get; private set; }
    internal long SourceBytes { get; private set; }
    internal long GeneratedSourceBytes { get; private set; }
    internal int Projects { get; private set; }
    internal int SyntaxTrees { get; private set; }
    internal int LoggerMessageDeclarations { get; private set; }
    internal int Events { get; private set; }
    internal int UnsupportedDeclarations { get; private set; }

    internal void ObserveProjectCount(int actual)
    {
        Projects = actual;
        if (actual > ProjectAnalysisLimits.MaxProjects)
        {
            throw new ProjectAnalysisException(ProjectAnalysisLimits.ProjectCountMessage(actual));
        }
    }

    internal void BeginProject()
    {
        currentProjectSourceDocuments = 0;
        currentProjectSourceBytes = 0;
        currentGeneratedSyntaxTrees = 0;
    }

    internal void ObserveSourceDocument(long bytes)
    {
        var projectDocuments = checked(currentProjectSourceDocuments + 1);
        var totalDocuments = checked(SourceDocuments + 1);
        if (projectDocuments > ProjectAnalysisLimits.MaxDocumentsPerProject)
        {
            throw new ProjectAnalysisException(ProjectAnalysisLimits.ProjectDocumentCountMessage(projectDocuments));
        }

        if (totalDocuments > ProjectAnalysisLimits.MaxDocuments)
        {
            throw new ProjectAnalysisException(ProjectAnalysisLimits.TotalDocumentCountMessage(totalDocuments));
        }

        if (bytes > ProjectAnalysisLimits.MaxSourceBytesPerDocument)
        {
            throw new ProjectAnalysisException(ProjectAnalysisLimits.SourceDocumentBytesMessage(bytes));
        }

        var projectBytes = checked(currentProjectSourceBytes + bytes);
        var totalBytes = checked(SourceBytes + bytes);
        if (projectBytes > ProjectAnalysisLimits.MaxSourceBytesPerProject)
        {
            throw new ProjectAnalysisException(ProjectAnalysisLimits.ProjectSourceBytesMessage(projectBytes));
        }

        if (totalBytes > ProjectAnalysisLimits.MaxSourceBytes)
        {
            throw new ProjectAnalysisException(ProjectAnalysisLimits.TotalSourceBytesMessage(totalBytes));
        }

        currentProjectSourceDocuments = projectDocuments;
        SourceDocuments = totalDocuments;
        currentProjectSourceBytes = projectBytes;
        SourceBytes = totalBytes;
    }

    internal void ObserveSyntaxTrees(int actual)
    {
        SyntaxTrees = actual;
        if (actual > ProjectAnalysisLimits.MaxSyntaxTreesPerProject)
        {
            throw new ProjectAnalysisException(ProjectAnalysisLimits.SyntaxTreeCountMessage(actual));
        }

    }

    internal void ObserveGeneratedSyntaxTree()
    {
        var generatedTrees = checked(currentGeneratedSyntaxTrees + 1);
        if (generatedTrees > ProjectAnalysisLimits.MaxGeneratedSyntaxTreesPerProject)
        {
            throw new ProjectAnalysisException(ProjectAnalysisLimits.GeneratedSyntaxTreeCountMessage(generatedTrees));
        }

        currentGeneratedSyntaxTrees = generatedTrees;
    }

    internal void ObserveGeneratedSourceBytes(long bytes)
    {
        if (bytes > ProjectAnalysisLimits.MaxGeneratedSourceBytesPerTree)
        {
            throw new ProjectAnalysisException(ProjectAnalysisLimits.GeneratedTreeBytesMessage(bytes));
        }

        var totalBytes = checked(GeneratedSourceBytes + bytes);
        if (totalBytes > ProjectAnalysisLimits.MaxGeneratedSourceBytes)
        {
            throw new ProjectAnalysisException(ProjectAnalysisLimits.TotalGeneratedBytesMessage(totalBytes));
        }

        GeneratedSourceBytes = totalBytes;
    }

    internal void EnsureGeneratedSourceBytesWithinBudget(SourceText text)
    {
        var encoding = text.Encoding ?? System.Text.Encoding.UTF8;
        var upperBound = checked((long)encoding.GetMaxByteCount(text.Length));
        if (upperBound > ProjectAnalysisLimits.MaxGeneratedSourceBytesPerTree)
        {
            throw new ProjectAnalysisException(ProjectAnalysisLimits.GeneratedTreeBytesMessage(upperBound));
        }

        var aggregateUpperBound = checked(GeneratedSourceBytes + upperBound);
        if (aggregateUpperBound > ProjectAnalysisLimits.MaxGeneratedSourceBytes)
        {
            throw new ProjectAnalysisException(ProjectAnalysisLimits.TotalGeneratedBytesMessage(aggregateUpperBound));
        }
    }

    internal void ObserveLoggerMessageDeclaration()
    {
        var count = checked(LoggerMessageDeclarations + 1);
        if (count > ProjectAnalysisLimits.MaxLoggerMessageDeclarations)
        {
            throw new ProjectAnalysisException(ProjectAnalysisLimits.LoggerMessageDeclarationCountMessage(count));
        }

        LoggerMessageDeclarations = count;
    }

    internal void ObserveEvent()
    {
        var count = checked(Events + 1);
        if (count > ProjectAnalysisLimits.MaxEvents)
        {
            throw new ProjectAnalysisException(ProjectAnalysisLimits.EventCountMessage(count));
        }

        Events = count;
    }

    internal void ObserveUnsupportedDeclaration()
    {
        var count = checked(UnsupportedDeclarations + 1);
        if (count > ProjectAnalysisLimits.MaxUnsupportedDeclarations)
        {
            throw new ProjectAnalysisException(ProjectAnalysisLimits.UnsupportedCountMessage(count));
        }

        UnsupportedDeclarations = count;
    }
}
