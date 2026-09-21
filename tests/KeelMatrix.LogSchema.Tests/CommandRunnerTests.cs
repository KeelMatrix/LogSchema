using KeelMatrix.LogSchema;

namespace KeelMatrix.LogSchema.Tests;

public sealed class CommandRunnerTests
{
    [Fact]
    public async Task HelpDocumentsCommandsAndReservedTelemetryFlag()
    {
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var exitCode = await CommandRunner.RunAsync(["--help"], output, errors);
        Assert.Equal(0, exitCode);
        Assert.Contains("logschema capture", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("--no-telemetry", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(errors.ToString());
    }

    [Fact]
    public async Task InvalidInvocationReturnsTwoAndJsonSeparatesToolErrors()
    {
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var exitCode = await CommandRunner.RunAsync(["diff", "one.json", "--format", "json"], output, errors);
        Assert.Equal(2, exitCode);
        Assert.Contains("toolErrors", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("two manifest files", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureWithZeroSupportedEventsFailsWithoutWritingBaseline()
    {
        var projectDirectory = Directory.CreateTempSubdirectory("logschema-zero-events-");
        try
        {
            var projectPath = Path.Combine(projectDirectory.FullName, "ZeroEvents.csproj");
            await File.WriteAllTextAsync(projectPath, """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>
""");
            await File.WriteAllTextAsync(Path.Combine(projectDirectory.FullName, "Empty.cs"), "public sealed class Empty { }");
            var baselinePath = Path.Combine(projectDirectory.FullName, "logschema.json");

            using var output = new StringWriter();
            using var errors = new StringWriter();
            var exitCode = await CommandRunner.RunAsync(["capture", projectPath, "--output", baselinePath, "--no-telemetry"], output, errors);

            Assert.Equal(3, exitCode);
            Assert.False(File.Exists(baselinePath));
            Assert.Contains("KMLOGP006", errors.ToString(), StringComparison.Ordinal);
            Assert.Contains("no supported [LoggerMessage] declarations", errors.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            projectDirectory.Delete(true);
        }
    }

    [Fact]
    public async Task CheckWithZeroEventBaselineFailsButDiffRemainsPureManifestComparison()
    {
        var projectPath = FindRepositoryFile("fixtures", "Phase0.Net8", "Phase0.Net8.csproj");
        var baselinePath = Path.Combine(Directory.CreateTempSubdirectory("logschema-zero-baseline-").FullName, "empty.json");
        try
        {
            await File.WriteAllTextAsync(baselinePath, """
{
  "schemaVersion": 1,
  "projects": [],
  "events": [],
  "unsupported": [],
  "analysisIssues": [],
  "compilationDiagnosticKinds": [],
  "workspaceDiagnosticKinds": []
}
""");

            using var checkOutput = new StringWriter();
            using var checkErrors = new StringWriter();
            var checkExitCode = await CommandRunner.RunAsync(["check", projectPath, "--baseline", baselinePath, "--no-telemetry"], checkOutput, checkErrors);
            Assert.Equal(3, checkExitCode);
            Assert.Contains("KMLOGP006", checkErrors.ToString(), StringComparison.Ordinal);
            Assert.Contains("baseline manifest contains zero events", checkErrors.ToString(), StringComparison.OrdinalIgnoreCase);

            using var diffOutput = new StringWriter();
            using var diffErrors = new StringWriter();
            var diffExitCode = await CommandRunner.RunAsync(["diff", baselinePath, baselinePath, "--no-telemetry"], diffOutput, diffErrors);
            Assert.Equal(0, diffExitCode);
            Assert.DoesNotContain("KMLOGP006", diffErrors.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(baselinePath)!, true);
        }
    }

    private static string FindRepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LogSchema.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine([directory!.FullName, .. parts]);
    }
}
