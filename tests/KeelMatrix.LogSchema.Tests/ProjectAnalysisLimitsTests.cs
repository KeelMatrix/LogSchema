using KeelMatrix.LogSchema;

namespace KeelMatrix.LogSchema.Tests;

public sealed class ProjectAnalysisLimitsTests
{
    [Fact]
    public void GraphAndSourceCeilingsAcceptNAndRejectNPlusOne()
    {
        var budget = new ProjectAnalysisBudget();
        budget.ObserveProjectCount(ProjectAnalysisLimits.MaxProjects);
        Assert.Throws<ProjectAnalysisException>(() => budget.ObserveProjectCount(ProjectAnalysisLimits.MaxProjects + 1));

        budget.BeginProject();
        for (var index = 0; index < ProjectAnalysisLimits.MaxDocumentsPerProject; index++)
        {
            budget.ObserveSourceDocument(1);
        }

        Assert.Throws<ProjectAnalysisException>(() => budget.ObserveSourceDocument(1));
    }

    [Fact]
    public void ByteAndSyntaxTreeCeilingsAcceptNAndRejectNPlusOne()
    {
        var budget = new ProjectAnalysisBudget();
        budget.BeginProject();
        budget.ObserveSourceDocument(ProjectAnalysisLimits.MaxSourceBytesPerDocument);
        Assert.Throws<ProjectAnalysisException>(() => budget.ObserveSourceDocument(ProjectAnalysisLimits.MaxSourceBytesPerDocument + 1));

        budget.BeginProject();
        budget.ObserveSyntaxTrees(ProjectAnalysisLimits.MaxSyntaxTreesPerProject);
        Assert.Throws<ProjectAnalysisException>(() => budget.ObserveSyntaxTrees(ProjectAnalysisLimits.MaxSyntaxTreesPerProject + 1));

        for (var index = 0; index < ProjectAnalysisLimits.MaxGeneratedSyntaxTreesPerProject; index++)
        {
            budget.ObserveGeneratedSyntaxTree();
        }

        Assert.Throws<ProjectAnalysisException>(() => budget.ObserveGeneratedSyntaxTree());

        var generatedBytes = new ProjectAnalysisBudget();
        generatedBytes.ObserveGeneratedSourceBytes(ProjectAnalysisLimits.MaxGeneratedSourceBytesPerTree);
        Assert.Throws<ProjectAnalysisException>(() => generatedBytes.ObserveGeneratedSourceBytes(ProjectAnalysisLimits.MaxGeneratedSourceBytesPerTree + 1));

        generatedBytes = new ProjectAnalysisBudget();
        for (var index = 0; index < ProjectAnalysisLimits.MaxGeneratedSourceBytes / ProjectAnalysisLimits.MaxGeneratedSourceBytesPerTree; index++)
        {
            generatedBytes.ObserveGeneratedSourceBytes(ProjectAnalysisLimits.MaxGeneratedSourceBytesPerTree);
        }

        Assert.Throws<ProjectAnalysisException>(() => generatedBytes.ObserveGeneratedSourceBytes(1));
    }

    [Fact]
    public void DeclarationEventAndUnsupportedCeilingsAcceptNAndRejectNPlusOne()
    {
        var declarations = new ProjectAnalysisBudget();
        for (var index = 0; index < ProjectAnalysisLimits.MaxLoggerMessageDeclarations; index++)
        {
            declarations.ObserveLoggerMessageDeclaration();
        }

        var declarationError = Assert.Throws<ProjectAnalysisException>(declarations.ObserveLoggerMessageDeclaration);
        Assert.Contains("Project analysis resource limit exceeded", declarationError.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, declarationError.Message);

        var events = new ProjectAnalysisBudget();
        for (var index = 0; index < ProjectAnalysisLimits.MaxEvents; index++)
        {
            events.ObserveEvent();
        }

        Assert.Throws<ProjectAnalysisException>(events.ObserveEvent);

        var unsupported = new ProjectAnalysisBudget();
        for (var index = 0; index < ProjectAnalysisLimits.MaxUnsupportedDeclarations; index++)
        {
            unsupported.ObserveUnsupportedDeclaration();
        }

        Assert.Throws<ProjectAnalysisException>(unsupported.ObserveUnsupportedDeclaration);
    }
}
