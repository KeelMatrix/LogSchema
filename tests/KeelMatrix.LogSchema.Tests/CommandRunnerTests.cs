using System.Text.Json;
using System.Text.Json.Nodes;
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
        Assert.Contains("contains no telemetry client", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Forms are recomputed from declared types", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Embedded forms and parameterForms must equal that vector", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("incomplete coverage and no findings", output.ToString(), StringComparison.Ordinal);
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
    public Task MissingBaselineAfterFormatPreservesJsonEnvelope() => AssertMissingOptionValuePreservesJsonEnvelope(["check", "Project.csproj", "--format", "json", "--baseline"]);

    [Fact]
    public Task MissingBaselineBeforeFormatPreservesJsonEnvelope() => AssertMissingOptionValuePreservesJsonEnvelope(["check", "Project.csproj", "--baseline", "--format", "json"]);

    private static async Task AssertMissingOptionValuePreservesJsonEnvelope(string[] args)
    {
        using var output = new StringWriter();
        using var errors = new StringWriter();

        var exitCode = await CommandRunner.RunAsync(args, output, errors);

        Assert.Equal(2, exitCode);
        Assert.Contains("\"toolErrors\"", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(errors.ToString());
    }

    [Fact]
    public async Task NumericFormatValueIsRejected()
    {
        using var output = new StringWriter();
        using var errors = new StringWriter();

        var exitCode = await CommandRunner.RunAsync(["diff", "one.json", "two.json", "--format", "99"], output, errors);

        Assert.Equal(2, exitCode);
        Assert.Contains("text or json", errors.ToString(), StringComparison.Ordinal);
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

    [Fact]
    public async Task UnsupportedCoverageGatesCheckAndReturnsDetails()
    {
        var projectPath = FindRepositoryFile("tests", "PackageConsumerFixture", "PackageConsumerFixture.csproj");
        var root = Directory.CreateTempSubdirectory("logschema-unsupported-");
        var capturedPath = Path.Combine(root.FullName, "captured.json");
        var baselinePath = Path.Combine(root.FullName, "baseline.json");
        try
        {
            using var captureOutput = new StringWriter();
            using var captureErrors = new StringWriter();
            Assert.Equal(0, await CommandRunner.RunAsync(["capture", projectPath, "--output", capturedPath, "--no-telemetry"], captureOutput, captureErrors));
            var captured = await ManifestJson.ReadAsync(capturedPath, CancellationToken.None);
            var projectKey = captured.Projects[0].Key;
            var baseline = captured with
            {
                Unsupported = [new UnsupportedDeclaration(projectKey, new SourceLocation("ConsumerLogging.cs", 1, "source"), "unsupported declaration", "PackageConsumerFixture.Unsupported", "unsupported form")]
            };
            await ManifestJson.WriteAsync(baseline, baselinePath, CancellationToken.None);

            using var output = new StringWriter();
            using var errors = new StringWriter();
            var exitCode = await CommandRunner.RunAsync(["check", projectPath, "--baseline", baselinePath, "--format", "json", "--no-telemetry"], output, errors);

            Assert.Equal(3, exitCode);
            Assert.Empty(errors.ToString());
            using var document = JsonDocument.Parse(output.ToString());
            Assert.Contains(document.RootElement.GetProperty("analysisErrors").EnumerateArray(), item => item.GetString()!.StartsWith("KMLOGP007:", StringComparison.Ordinal));
            var unsupported = Assert.Single(document.RootElement.GetProperty("unsupported").EnumerateArray());
            Assert.Equal("PackageConsumerFixture.Unsupported", unsupported.GetProperty("declarationKey").GetString());
            Assert.Equal("unsupported form", unsupported.GetProperty("reason").GetString());
            Assert.False(document.RootElement.GetProperty("coverageComplete").GetBoolean());
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task UnavailableOutputDestinationReturnsJsonAnalysisEnvelope()
    {
        var projectPath = FindRepositoryFile("tests", "PackageConsumerFixture", "PackageConsumerFixture.csproj");
        var root = Directory.CreateTempSubdirectory("logschema-cli-output-");
        try
        {
            using var output = new StringWriter();
            using var errors = new StringWriter();
            var exitCode = await CommandRunner.RunAsync(["capture", projectPath, "--output", root.FullName, "--format", "json", "--no-telemetry"], output, errors);

            Assert.Equal(3, exitCode);
            Assert.Empty(errors.ToString());
            Assert.Contains("analysisErrors", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(root.FullName, output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("at KeelMatrix", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task InvalidProjectPathReturnsJsonAnalysisEnvelope()
    {
        using var output = new StringWriter();
        using var errors = new StringWriter();

        var exitCode = await CommandRunner.RunAsync(["check", "\0", "--baseline", "baseline.json", "--format", "json", "--no-telemetry"], output, errors);

        Assert.Equal(3, exitCode);
        Assert.Empty(errors.ToString());
        Assert.Contains("analysisErrors", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("at KeelMatrix", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Event", 1, "string")]
    [InlineData("DerivedException", 1, "DerivedProblem")]
    public async Task SymmetricCoordinatedParameterFormForgeriesFailClosed(string method, int parameterIndex, string parameterType)
    {
        var root = Directory.CreateTempSubdirectory("logschema-symmetric-form-");
        var capturedPath = Path.Combine(root.FullName, "captured.json");
        var forgedPath = Path.Combine(root.FullName, "forged.json");
        try
        {
            await CaptureConsumerFixtureAsync(capturedPath);
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(capturedPath))!;
            var @event = manifest["events"]!.AsArray().Single(item => item!["method"]!.GetValue<string>() == method)!;
            @event["identity"] = @event["identity"]!.GetValue<string>().Replace(":None:" + parameterType, ":Exception:" + parameterType, StringComparison.Ordinal);
            @event["parameterForms"]![parameterIndex] = "Exception";
            await File.WriteAllTextAsync(forgedPath, manifest.ToJsonString());

            using var output = new StringWriter();
            using var errors = new StringWriter();
            var exitCode = await CommandRunner.RunAsync(["diff", forgedPath, forgedPath, "--format", "json", "--severity", "all", "--no-telemetry"], output, errors);

            AssertAnalysisErrorEnvelope(exitCode, output.ToString(), errors.ToString(), root.FullName);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task ComparisonAnalysisErrorsReportIncompleteCoverage()
    {
        var root = Directory.CreateTempSubdirectory("logschema-comparison-form-");
        var capturedPath = Path.Combine(root.FullName, "captured.json");
        var forgedPath = Path.Combine(root.FullName, "forged.json");
        try
        {
            await CaptureConsumerFixtureAsync(capturedPath);
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(capturedPath))!;
            var projectKey = manifest["projects"]![0]!["key"]!.GetValue<string>();
            manifest["analysisIssues"] = new JsonArray(new JsonObject
            {
                ["projectKey"] = projectKey,
                ["code"] = "KMLOGP001",
                ["severity"] = "error",
                ["message"] = "Synthetic comparison analysis error.",
                ["declarationKey"] = string.Empty,
                ["sources"] = new JsonArray()
            });
            await File.WriteAllTextAsync(forgedPath, manifest.ToJsonString());
            _ = await ManifestJson.ReadAsync(forgedPath, CancellationToken.None);

            foreach (var arguments in new[]
            {
                new[] { "check", FindRepositoryFile("tests", "PackageConsumerFixture", "PackageConsumerFixture.csproj"), "--baseline", forgedPath, "--format", "json", "--severity", "all", "--no-telemetry" },
                new[] { "diff", capturedPath, forgedPath, "--format", "json", "--severity", "all", "--no-telemetry" }
            })
            {
                using var output = new StringWriter();
                using var errors = new StringWriter();
                var exitCode = await CommandRunner.RunAsync(arguments, output, errors);

                AssertAnalysisErrorEnvelope(exitCode, output.ToString(), errors.ToString(), root.FullName);
                Assert.Contains("KMLOGP001: Synthetic comparison analysis error.", output.ToString(), StringComparison.Ordinal);
            }
        }
        finally
        {
            root.Delete(true);
        }
    }

    private static async Task CaptureConsumerFixtureAsync(string outputPath)
    {
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var exitCode = await CommandRunner.RunAsync(["capture", FindRepositoryFile("tests", "PackageConsumerFixture", "PackageConsumerFixture.csproj"), "--output", outputPath, "--no-telemetry"], output, errors);
        Assert.Equal(0, exitCode);
        Assert.Empty(errors.ToString());
    }

    private static void AssertAnalysisErrorEnvelope(int exitCode, string output, string errors, string forbiddenPath)
    {
        Assert.Equal(3, exitCode);
        Assert.Empty(errors);
        using var document = JsonDocument.Parse(output);
        Assert.Empty(document.RootElement.GetProperty("toolErrors").EnumerateArray());
        Assert.NotEmpty(document.RootElement.GetProperty("analysisErrors").EnumerateArray());
        Assert.Empty(document.RootElement.GetProperty("findings").EnumerateArray());
        Assert.False(document.RootElement.GetProperty("coverageComplete").GetBoolean());
        Assert.DoesNotContain(forbiddenPath, output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("at KeelMatrix", output, StringComparison.Ordinal);
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
