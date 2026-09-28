using System.Text.Json;
using System.Text.Json.Nodes;
using KeelMatrix.LogSchema;

namespace KeelMatrix.LogSchema.Tests;

public sealed class ManifestIntegrityTests
{
    [Fact]
    public async Task CanonicalManifestRoundTripCarriesIntegrityAndRemainsComparable()
    {
        var root = Directory.CreateTempSubdirectory("logschema-integrity-roundtrip-");
        var path = Path.Combine(root.FullName, "manifest.json");
        try
        {
            await ManifestJson.WriteAsync(CreateManifest() with
            {
                Unsupported = [],
                AnalysisIssues = [],
                CompilationDiagnosticKinds = [],
                WorkspaceDiagnosticKinds = []
            }, path, CancellationToken.None);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var integrity = document.RootElement.GetProperty("integrity").GetString();
            Assert.NotNull(integrity);
            Assert.Matches("^[0-9A-F]{64}$", integrity!);

            var roundTripped = await ManifestJson.ReadAsync(path, CancellationToken.None);
            Assert.Equal(1, roundTripped.SchemaVersion);
            Assert.Equal(integrity, roundTripped.Integrity);

            using var output = new StringWriter();
            using var errors = new StringWriter();
            var exitCode = await CommandRunner.RunAsync(["diff", path, path, "--format", "json", "--severity", "all", "--no-telemetry"], output, errors);
            Assert.Equal(0, exitCode);
            Assert.Empty(errors.ToString());
            using var envelope = JsonDocument.Parse(output.ToString());
            Assert.Empty(envelope.RootElement.GetProperty("findings").EnumerateArray());
            Assert.True(envelope.RootElement.GetProperty("coverageComplete").GetBoolean());
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task EveryPersistedManifestFieldIsCoveredByReadTimeIntegrityGuard()
    {
        var root = Directory.CreateTempSubdirectory("logschema-integrity-matrix-");
        var validPath = Path.Combine(root.FullName, "valid.json");
        await ManifestJson.WriteAsync(CreateManifest(), validPath, CancellationToken.None);
        var validJson = await File.ReadAllTextAsync(validPath);

        var mutations = new (string Name, Action<JsonNode> Mutate)[]
        {
            ("schemaVersion", node => node["schemaVersion"] = 2),
            ("project.key", node => node["projects"]![0]!["key"] = "Other|net8.0"),
            ("project.name", node => node["projects"]![0]!["name"] = "Other"),
            ("project.assembly", node => node["projects"]![0]!["assembly"] = "Other"),
            ("project.targetFramework", node => node["projects"]![0]!["targetFramework"] = "net9.0"),
            ("event.projectKey", node => node["events"]![0]!["projectKey"] = "Other|net8.0"),
            ("event.identity", node => node["events"]![0]!["identity"] = "P.Logging.Other`0(None:ILogger:Microsoft.Extensions.Logging.ILogger,None:None:int)"),
            ("event.containingType", node => node["events"]![0]!["containingType"] = "P.Other"),
            ("event.method", node => node["events"]![0]!["method"] = "Other"),
            ("event.genericArity", node => node["events"]![0]!["genericArity"] = 1),
            ("event.parameterRefKinds", node => node["events"]![0]!["parameterRefKinds"]![1] = "Ref"),
            ("event.parameterRefKinds.order", node => node["events"]![0]!["parameterRefKinds"] = JsonNode.Parse("[\"None\",\"Ref\",\"None\"]")),
            ("event.eventId", node => node["events"]![0]!["eventId"] = 2),
            ("event.eventName", node => node["events"]![0]!["eventName"] = "Other"),
            ("event.level", node => node["events"]![0]!["level"] = "Warning"),
            ("event.message", node => node["events"]![0]!["message"] = "Other {Value,10:000}"),
            ("event.placeholder.name", node => node["events"]![0]!["placeholders"]![0]!["name"] = "Other"),
            ("event.placeholder.token", node => node["events"]![0]!["placeholders"]![0]!["token"] = "Other"),
            ("event.placeholders.missing", node => node["events"]![0]!["placeholders"] = JsonNode.Parse("[{\"name\":\"Value\",\"token\":\"Value,10:000\"}]")),
            ("event.placeholders.extra", node => node["events"]![0]!["placeholders"] = JsonNode.Parse("[{\"name\":\"Value\",\"token\":\"Value,10:000\"},{\"name\":\"Other\",\"token\":\"Other\"},{\"name\":\"Other\",\"token\":\"Other\"}]")),
            ("event.placeholders.order", node => node["events"]![0]!["placeholders"] = JsonNode.Parse("[{\"name\":\"Other\",\"token\":\"Other\"},{\"name\":\"Value\",\"token\":\"Value,10:000\"}]")),
            ("event.parameterForms", node => node["events"]![0]!["parameterForms"]![1] = "Exception"),
            ("event.source.file", node => node["events"]![0]!["source"]!["file"] = "project/Other.cs"),
            ("event.source.line", node => node["events"]![0]!["source"]!["line"] = 2),
            ("event.source.kind", node => node["events"]![0]!["source"]!["kind"] = "generated"),
            ("event.parameter.name", node => node["events"]![0]!["parameters"]![1]!["name"] = "other"),
            ("event.parameter.type", node => node["events"]![0]!["parameters"]![1]!["type"] = "long"),
            ("event.parameter.refKind", node => node["events"]![0]!["parameters"]![1]!["refKind"] = "Ref"),
            ("event.parameter.role", node => node["events"]![0]!["parameters"]![1]!["role"] = "Exception"),
            ("event.parameters.order", node => node["events"]![0]!["parameters"] = JsonNode.Parse("[{\"name\":\"logger\",\"type\":\"Microsoft.Extensions.Logging.ILogger\",\"refKind\":\"None\",\"role\":\"Logger\"},{\"name\":\"other\",\"type\":\"string\",\"refKind\":\"Ref\",\"role\":\"State\"},{\"name\":\"value\",\"type\":\"int\",\"refKind\":\"None\",\"role\":\"State\"}]")),
            ("event.structuredState.parameterName", node => node["events"]![0]!["structuredState"]![0]!["parameterName"] = "other"),
            ("event.structuredState.emittedName", node => node["events"]![0]!["structuredState"]![0]!["emittedName"] = "Other"),
            ("event.structuredState.order", node => node["events"]![0]!["structuredState"] = JsonNode.Parse("[{\"parameterName\":\"other\",\"emittedName\":\"Other\"},{\"parameterName\":\"value\",\"emittedName\":\"Value\"}]")),
            ("event.loggerParameter", node => node["events"]![0]!["loggerParameter"] = "other"),
            ("event.exceptionParameter", node => node["events"]![0]!["exceptionParameter"] = "value"),
            ("event.levelSource", node => node["events"]![0]!["levelSource"] = "Dynamic"),
            ("event.levelParameter", node => node["events"]![0]!["levelParameter"] = "value"),
            ("unsupported.projectKey", node => node["unsupported"]![0]!["projectKey"] = "Other|net8.0"),
            ("unsupported.source.file", node => node["unsupported"]![0]!["source"]!["file"] = "project/Other.cs"),
            ("unsupported.source.line", node => node["unsupported"]![0]!["source"]!["line"] = 2),
            ("unsupported.source.kind", node => node["unsupported"]![0]!["source"]!["kind"] = "generated"),
            ("unsupported.declaration", node => node["unsupported"]![0]!["declaration"] = "other"),
            ("unsupported.declarationKey", node => node["unsupported"]![0]!["declarationKey"] = "Other"),
            ("unsupported.reason", node => node["unsupported"]![0]!["reason"] = "other"),
            ("analysisIssue.projectKey", node => node["analysisIssues"]![0]!["projectKey"] = "Other|net8.0"),
            ("analysisIssue.code", node => node["analysisIssues"]![0]!["code"] = "OTHER"),
            ("analysisIssue.severity", node => node["analysisIssues"]![0]!["severity"] = "error"),
            ("analysisIssue.message", node => node["analysisIssues"]![0]!["message"] = "other"),
            ("analysisIssue.declarationKey", node => node["analysisIssues"]![0]!["declarationKey"] = "Other"),
            ("analysisIssue.source.file", node => node["analysisIssues"]![0]!["sources"]![0]!["file"] = "project/Other.cs"),
            ("analysisIssue.source.line", node => node["analysisIssues"]![0]!["sources"]![0]!["line"] = 2),
            ("analysisIssue.source.kind", node => node["analysisIssues"]![0]!["sources"]![0]!["kind"] = "generated"),
            ("compilationDiagnosticKinds", node => node["compilationDiagnosticKinds"]![0] = "OTHER"),
            ("workspaceDiagnosticKinds", node => node["workspaceDiagnosticKinds"]![0] = "OTHER"),
            ("integrity", node => node["integrity"] = new string('0', 64))
        };

        try
        {
            foreach (var (name, mutate) in mutations)
            {
                var candidate = JsonNode.Parse(validJson)!;
                mutate(candidate);
                var path = Path.Combine(root.FullName, name.Replace('.', '-') + ".json");
                await File.WriteAllTextAsync(path, candidate.ToJsonString());

                using var output = new StringWriter();
                using var errors = new StringWriter();
                var exitCode = await CommandRunner.RunAsync(["diff", path, path, "--format", "json", "--severity", "all", "--no-telemetry"], output, errors);
                Assert.Equal(3, exitCode);
                Assert.Empty(errors.ToString());
                using var envelope = JsonDocument.Parse(output.ToString());
                Assert.NotEmpty(envelope.RootElement.GetProperty("analysisErrors").EnumerateArray());
                Assert.Empty(envelope.RootElement.GetProperty("findings").EnumerateArray());
                Assert.False(envelope.RootElement.GetProperty("coverageComplete").GetBoolean());
            }
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task MissingIntegrityFailsClosedBeforeComparison()
    {
        var root = Directory.CreateTempSubdirectory("logschema-integrity-required-");
        var validPath = Path.Combine(root.FullName, "valid.json");
        var unsignedPath = Path.Combine(root.FullName, "unsigned.json");
        try
        {
            await ManifestJson.WriteAsync(CreateManifest(), validPath, CancellationToken.None);
            var unsigned = JsonNode.Parse(await File.ReadAllTextAsync(validPath))!;
            unsigned.AsObject().Remove("integrity");
            await File.WriteAllTextAsync(unsignedPath, unsigned.ToJsonString());

            using var output = new StringWriter();
            using var errors = new StringWriter();
            var exitCode = await CommandRunner.RunAsync(["diff", unsignedPath, unsignedPath, "--format", "json", "--severity", "all", "--no-telemetry"], output, errors);
            Assert.Equal(3, exitCode);
            Assert.Empty(errors.ToString());
            using var envelope = JsonDocument.Parse(output.ToString());
            Assert.Contains("unsigned legacy v1", envelope.RootElement.GetProperty("analysisErrors")[0].GetString(), StringComparison.Ordinal);
            Assert.Empty(envelope.RootElement.GetProperty("findings").EnumerateArray());
            Assert.False(envelope.RootElement.GetProperty("coverageComplete").GetBoolean());
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task UnknownManifestFieldsFailClosedEvenWhenKnownContentIsValid()
    {
        var root = Directory.CreateTempSubdirectory("logschema-integrity-unknown-");
        var validPath = Path.Combine(root.FullName, "valid.json");
        var unknownPath = Path.Combine(root.FullName, "unknown.json");
        try
        {
            await ManifestJson.WriteAsync(CreateManifest(), validPath, CancellationToken.None);
            var unknown = JsonNode.Parse(await File.ReadAllTextAsync(validPath))!;
            unknown.AsObject()["futureField"] = "not part of schema v1";
            await File.WriteAllTextAsync(unknownPath, unknown.ToJsonString());

            await Assert.ThrowsAsync<ManifestReadException>(() => ManifestJson.ReadAsync(unknownPath, CancellationToken.None));
        }
        finally
        {
            root.Delete(true);
        }
    }

    private static ManifestDocument CreateManifest() => new(
        1,
        [new ProjectIdentity("P|net8.0", "P", "P", "net8.0")],
        [new EventContract(
            "P|net8.0",
            "P.Logging.Event`0(None:ILogger:Microsoft.Extensions.Logging.ILogger,None:None:int,Ref:None:string)",
            "P.Logging",
            "Event",
            0,
            ["None", "None", "Ref"],
            1,
            "Event",
            "Information",
            "Processed {Value,10:000} {Other}",
            [new Placeholder("Value", "Value,10:000"), new Placeholder("Other", "Other")],
            ["ILogger", "None", "None"],
            new SourceLocation("project/Logging.cs", 1, "source"),
            [
                new ParameterContract("logger", "Microsoft.Extensions.Logging.ILogger", "None", "Logger"),
                new ParameterContract("value", "int", "None", "State"),
                new ParameterContract("other", "string", "Ref", "State")
            ],
            [new StructuredStateProperty("value", "Value"), new StructuredStateProperty("other", "Other")],
            "logger",
            null,
            "Fixed",
            null)],
        [new UnsupportedDeclaration("P|net8.0", new SourceLocation("project/Unsupported.cs", 2, "source"), "unsupported declaration", "P.Unsupported", "unsupported reason")],
        [new AnalysisIssue("P|net8.0", "KMLOGP002", "warning", "unsupported declaration warning", "P.Unsupported", [new SourceLocation("project/Unsupported.cs", 2, "source")])],
        ["CS0000"],
        ["MSB0000"]);
}
