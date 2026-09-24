namespace KeelMatrix.LogSchema;

internal static class ProjectAnalysisLimits
{
    internal const int MaxProjects = 64;
    internal const int MaxDocumentsPerProject = 512;
    internal const int MaxDocuments = 4096;

    internal static string ProjectCountMessage(int actual) =>
        $"Project analysis resource limit exceeded: the input contains {actual} projects; the maximum supported is {MaxProjects}.";

    internal static string ProjectDocumentCountMessage(string projectName, int actual) =>
        $"Project analysis resource limit exceeded: project '{projectName}' contains {actual} source documents; the maximum supported per project is {MaxDocumentsPerProject}.";

    internal static string TotalDocumentCountMessage(int actual) =>
        $"Project analysis resource limit exceeded: the input contains {actual} source documents; the maximum supported total is {MaxDocuments}.";
}
