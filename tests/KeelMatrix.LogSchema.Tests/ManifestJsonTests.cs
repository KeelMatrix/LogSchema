using System.Text;
using System.Text.Json.Nodes;
using KeelMatrix.LogSchema;

namespace KeelMatrix.LogSchema.Tests;

public sealed class ManifestJsonTests
{
    [Fact]
    public async Task SerializationIsCanonicalUtf8AndPathIndependent()
    {
        var manifest = new ManifestDocument(1, [new ProjectIdentity("P|net8.0", "P", "P", "net8.0")], [new EventContract("P|net8.0", "P.Logging.Event`0(None:Microsoft.Extensions.Logging.ILogger)", "P.Logging", "Event", 0, ["None"], 10, "Event", "Information", "Событие {Идентификатор}", [new Placeholder("Идентификатор", "Идентификатор")], ["ILogger"], new SourceLocation("src/Logging.cs", 4, "source"))], [], [], [], []);
        var root = Path.Combine(Path.GetTempPath(), "logschema-tests-" + Guid.NewGuid().ToString("N"));
        var first = Path.Combine(root, "one", "logschema.json");
        var second = Path.Combine(root, "two", "logschema.json");
        try
        {
            await ManifestJson.WriteAsync(manifest, first, CancellationToken.None);
            await ManifestJson.WriteAsync(manifest, second, CancellationToken.None);
            Assert.Equal(await File.ReadAllBytesAsync(first), await File.ReadAllBytesAsync(second));
            Assert.Equal(1, (await ManifestJson.ReadAsync(first, CancellationToken.None)).SchemaVersion);
            var bytes = await File.ReadAllBytesAsync(first);
            Assert.True(bytes.Length == 0 || bytes[0] != 0xEF);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task FutureSchemaAndMalformedInputFailClosed()
    {
        var root = Path.Combine(Path.GetTempPath(), "logschema-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var future = Path.Combine(root, "future.json");
        var stringVersion = Path.Combine(root, "string-version.json");
        var fractionalVersion = Path.Combine(root, "fractional-version.json");
        var malformed = Path.Combine(root, "malformed.json");
        await File.WriteAllTextAsync(future, """
        {
          "schemaVersion": 99,
          "projects": [],
          "events": [],
          "unsupported": [],
          "analysisIssues": [],
          "compilationDiagnosticKinds": [],
          "workspaceDiagnosticKinds": []
        }
        """);
        await File.WriteAllTextAsync(stringVersion, "{\"schemaVersion\":\"1\",\"projects\":[],\"events\":[],\"unsupported\":[],\"analysisIssues\":[],\"compilationDiagnosticKinds\":[],\"workspaceDiagnosticKinds\":[]}");
        await File.WriteAllTextAsync(fractionalVersion, "{\"schemaVersion\":1.0,\"projects\":[],\"events\":[],\"unsupported\":[],\"analysisIssues\":[],\"compilationDiagnosticKinds\":[],\"workspaceDiagnosticKinds\":[]}");
        await File.WriteAllTextAsync(malformed, "{\"schemaVersion\":1");
        try
        {
            await Assert.ThrowsAsync<ManifestReadException>(() => ManifestJson.ReadAsync(future, CancellationToken.None));
            await Assert.ThrowsAsync<ManifestReadException>(() => ManifestJson.ReadAsync(stringVersion, CancellationToken.None));
            await Assert.ThrowsAsync<ManifestReadException>(() => ManifestJson.ReadAsync(fractionalVersion, CancellationToken.None));
            await Assert.ThrowsAsync<ManifestReadException>(() => ManifestJson.ReadAsync(malformed, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task OversizedAndDuplicateIdentityInputsFailClosed()
    {
        var root = Path.Combine(Path.GetTempPath(), "logschema-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var oversized = Path.Combine(root, "oversized.json");
        var duplicate = Path.Combine(root, "duplicate.json");
        await File.WriteAllTextAsync(oversized, new string('x', ManifestJson.MaxBytes + 1));
        await File.WriteAllTextAsync(duplicate, """
        {
          "schemaVersion": 1,
          "projects": [
            { "key": "P|net8.0", "name": "P", "assembly": "P", "targetFramework": "net8.0" },
            { "key": "P|net8.0", "name": "P", "assembly": "P", "targetFramework": "net8.0" }
          ],
          "events": [],
          "unsupported": [],
          "analysisIssues": [],
          "compilationDiagnosticKinds": [],
          "workspaceDiagnosticKinds": []
        }
        """);
        try
        {
            await Assert.ThrowsAsync<ManifestReadException>(() => ManifestJson.ReadAsync(oversized, CancellationToken.None));
            await Assert.ThrowsAsync<ManifestReadException>(() => ManifestJson.ReadAsync(duplicate, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RepresentativeLargeManifestRoundTripsWithinResourceLimits()
    {
        const int eventCount = 4_000;
        var root = Directory.CreateTempSubdirectory("logschema-large-");
        var path = Path.Combine(root.FullName, "large.json");
        var events = Enumerable.Range(0, eventCount)
            .Select(index => new EventContract(
                "Large|net8.0",
                $"Large.Logging.Event{index}`0(None:Microsoft.Extensions.Logging.ILogger,None:int)",
                "Large.Logging",
                $"Event{index}",
                0,
                ["None", "None"],
                index,
                $"Event{index}",
                "Information",
                $"Processed {{Value{index}}}",
                [new Placeholder($"Value{index}", $"Value{index}")],
                ["ILogger", "None"],
                new SourceLocation($"Logging/Event{index}.cs", index + 1, "source")))
            .ToArray();
        var manifest = new ManifestDocument(
            1,
            [new ProjectIdentity("Large|net8.0", "Large", "Large", "net8.0")],
            events,
            [],
            [],
            [],
            []);

        try
        {
            await ManifestJson.WriteAsync(manifest, path, CancellationToken.None);
            var length = new FileInfo(path).Length;
            Assert.InRange(length, 1, ManifestJson.MaxBytes);

            var roundTripped = await ManifestJson.ReadAsync(path, CancellationToken.None);
            Assert.Equal(eventCount, roundTripped.Events.Count);
            Assert.Contains(roundTripped.Events, item => item.Method == "Event0");
            Assert.Contains(roundTripped.Events, item => item.Method == $"Event{eventCount - 1}");
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task ExcessiveJsonDepthFailsClosed()
    {
        var root = Directory.CreateTempSubdirectory("logschema-depth-");
        var path = Path.Combine(root.FullName, "deep.json");
        var nested = new string('[', 40) + "0" + new string(']', 40);
        await File.WriteAllTextAsync(path, "{\"schemaVersion\":1,\"projects\":[],\"events\":[],\"unsupported\":[],\"analysisIssues\":[],\"compilationDiagnosticKinds\":[],\"workspaceDiagnosticKinds\":[],\"extra\":" + nested + "}");

        try
        {
            await Assert.ThrowsAsync<ManifestReadException>(() => ManifestJson.ReadAsync(path, CancellationToken.None));
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task MissingRequiredArraysAndEventFieldsFailClosed()
    {
        var root = Directory.CreateTempSubdirectory("logschema-incomplete-");
        var missingArrays = Path.Combine(root.FullName, "missing-arrays.json");
        var incompleteEvent = Path.Combine(root.FullName, "incomplete-event.json");
        await File.WriteAllTextAsync(missingArrays, "{\"schemaVersion\":1,\"projects\":[],\"events\":[],\"unsupported\":[],\"analysisIssues\":[]}");
        await File.WriteAllTextAsync(incompleteEvent, """
        {
          "schemaVersion": 1,
          "projects": [{ "key": "P|net8.0", "name": "P", "assembly": "P", "targetFramework": "net8.0" }],
          "events": [{
            "projectKey": "P|net8.0",
            "identity": "P.Logging.Event",
            "containingType": "P.Logging",
            "method": "Event",
            "genericArity": 0,
            "parameterRefKinds": ["None"],
            "eventName": "Event",
            "level": "Information",
            "message": "Event",
            "placeholders": [],
            "parameterForms": ["ILogger"],
            "source": { "file": "Logging.cs", "line": 1, "kind": "source" }
          }],
          "unsupported": [],
          "analysisIssues": [],
          "compilationDiagnosticKinds": [],
          "workspaceDiagnosticKinds": []
        }
        """);
        try
        {
            await Assert.ThrowsAsync<ManifestReadException>(() => ManifestJson.ReadAsync(missingArrays, CancellationToken.None));
            await Assert.ThrowsAsync<ManifestReadException>(() => ManifestJson.ReadAsync(incompleteEvent, CancellationToken.None));
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task ContradictoryCanonicalIdentityTupleFailsClosed()
    {
        var root = Directory.CreateTempSubdirectory("logschema-identity-");
        var path = Path.Combine(root.FullName, "contradictory.json");
        await File.WriteAllTextAsync(path, """
        {
          "schemaVersion": 1,
          "projects": [{ "key": "MyService|net8.0", "name": "MyService", "assembly": "MyService", "targetFramework": "net8.0" }],
          "events": [{
            "projectKey": "MyService|net8.0",
            "identity": "Logging.Event`0(None:Microsoft.Extensions.Logging.ILogger,None:string,None:string,None:string)",
            "containingType": "Totally.Wrong.Type",
            "method": "WrongMethod",
            "genericArity": 99,
            "parameterRefKinds": ["UnknownRefKind"],
            "eventId": 1,
            "eventName": "Event",
            "level": "Information",
            "message": "Event {One} {Two} {Three}",
            "placeholders": [
              { "name": "One", "token": "One" },
              { "name": "Two", "token": "Two" },
              { "name": "Three", "token": "Three" }
            ],
            "parameterForms": ["UnknownParameterForm"],
            "source": { "file": "Logging.cs", "line": 1, "kind": "source" }
          }],
          "unsupported": [],
          "analysisIssues": [],
          "compilationDiagnosticKinds": [],
          "workspaceDiagnosticKinds": []
        }
        """);

        try
        {
            await Assert.ThrowsAsync<ManifestReadException>(() => ManifestJson.ReadAsync(path, CancellationToken.None));
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Theory]
    [InlineData("containingType")]
    [InlineData("method")]
    [InlineData("genericArity")]
    [InlineData("parameterCount")]
    [InlineData("refKindMismatch")]
    [InlineData("unknownRefKind")]
    [InlineData("parameterFormMismatch")]
    [InlineData("additiveContainingType")]
    [InlineData("additiveMethod")]
    [InlineData("additiveGenericArity")]
    [InlineData("additiveParameterCount")]
    [InlineData("additiveFormException")]
    [InlineData("additiveFormILogger")]
    [InlineData("additiveFormLogLevel")]
    [InlineData("additiveFormNone")]
    [InlineData("unknownParameterForm")]
    [InlineData("malformedIdentity")]
    [InlineData("invalidTypeSyntax")]
    public async Task EachNonCanonicalIdentityDimensionFailsClosed(string mutation)
    {
        var root = Directory.CreateTempSubdirectory("logschema-identity-dimension-");
        var path = Path.Combine(root.FullName, mutation + ".json");
        var manifest = JsonNode.Parse("""
        {
          "schemaVersion": 1,
          "projects": [{ "key": "MyService|net8.0", "name": "MyService", "assembly": "MyService", "targetFramework": "net8.0" }],
          "events": [{
            "projectKey": "MyService|net8.0",
            "identity": "Logging.Event`0(None:Microsoft.Extensions.Logging.ILogger,None:string,None:string,None:string)",
            "containingType": "Logging",
            "method": "Event",
            "genericArity": 0,
            "parameterRefKinds": ["None", "None", "None", "None"],
            "eventId": 1,
            "eventName": "Event",
            "level": "Information",
            "message": "Event {One} {Two} {Three}",
            "placeholders": [
              { "name": "One", "token": "One" },
              { "name": "Two", "token": "Two" },
              { "name": "Three", "token": "Three" }
            ],
            "parameterForms": ["ILogger", "None", "None", "None"],
            "source": { "file": "Logging.cs", "line": 1, "kind": "source" }
          }],
          "unsupported": [],
          "analysisIssues": [],
          "compilationDiagnosticKinds": [],
          "workspaceDiagnosticKinds": []
        }
        """)!;
        var eventNode = manifest["events"]![0]!.AsObject();
        switch (mutation)
        {
            case "containingType": eventNode["containingType"] = "Wrong.Type"; break;
            case "method": eventNode["method"] = "WrongMethod"; break;
            case "genericArity": eventNode["genericArity"] = 1; break;
            case "parameterCount": eventNode["parameterRefKinds"] = JsonNode.Parse("""["None"]"""); break;
            case "refKindMismatch": eventNode["parameterRefKinds"] = JsonNode.Parse("""["None","Ref","None","None"]"""); break;
            case "unknownRefKind": eventNode["parameterRefKinds"] = JsonNode.Parse("""["None","UnknownRefKind","None","None"]"""); break;
            case "parameterFormMismatch": eventNode["parameterForms"] = JsonNode.Parse("""["ILogger","ILogger","None","None"]"""); break;
            case "additiveContainingType": eventNode["containingType"] = "Logging.Extra"; break;
            case "additiveMethod": eventNode["method"] = "EventExtra"; break;
            case "additiveGenericArity": eventNode["genericArity"] = 1; break;
            case "additiveParameterCount": eventNode["parameterRefKinds"] = JsonNode.Parse("""["None","None","None","None","None"]"""); break;
            case "additiveFormException": eventNode["parameterForms"] = JsonNode.Parse("""["ILogger","None","None","None","Exception"]"""); break;
            case "additiveFormILogger": eventNode["parameterForms"] = JsonNode.Parse("""["ILogger","None","None","None","ILogger"]"""); break;
            case "additiveFormLogLevel": eventNode["parameterForms"] = JsonNode.Parse("""["ILogger","None","None","None","LogLevel"]"""); break;
            case "additiveFormNone": eventNode["parameterForms"] = JsonNode.Parse("""["ILogger","None","None","None","None"]"""); break;
            case "unknownParameterForm": eventNode["parameterForms"] = JsonNode.Parse("""["UnknownParameterForm"]"""); break;
            case "malformedIdentity": eventNode["identity"] = "Logging.Event`0(None:Microsoft.Extensions.Logging.ILogger"; break;
            case "invalidTypeSyntax": eventNode["identity"] = "Logging.Event`0(None:Microsoft.Extensions.Logging.ILogger,None:string,None:string,None:???)"; break;
            default: throw new InvalidOperationException("Unknown test mutation.");
        }
        await File.WriteAllTextAsync(path, manifest.ToJsonString());

        try
        {
            await Assert.ThrowsAsync<ManifestReadException>(() => ManifestJson.ReadAsync(path, CancellationToken.None));
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task FailedOversizedWriteDoesNotReplaceExistingManifest()
    {
        var root = Directory.CreateTempSubdirectory("logschema-write-");
        var path = Path.Combine(root.FullName, "logschema.json");
        await File.WriteAllTextAsync(path, "existing baseline");
        var huge = new string('x', ManifestJson.MaxBytes);
        var manifest = new ManifestDocument(
            1,
            [new ProjectIdentity("P|net8.0", "P", "P", "net8.0")],
            [new EventContract("P|net8.0", "P.Event`0(None:Microsoft.Extensions.Logging.ILogger)", "P", "Event", 0, ["None"], 1, "Event", "Information", huge, [], ["ILogger"], new SourceLocation("Logging.cs", 1, "source"))],
            [], [], [], []);
        try
        {
            await Assert.ThrowsAsync<ManifestWriteException>(() => ManifestJson.WriteAsync(manifest, path, CancellationToken.None));
            Assert.Equal("existing baseline", await File.ReadAllTextAsync(path));
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task IncompleteOutputDestinationFailsWithoutReplacingDirectory()
    {
        var root = Directory.CreateTempSubdirectory("logschema-output-");
        var destination = Path.Combine(root.FullName, "destination");
        Directory.CreateDirectory(destination);
        var manifest = new ManifestDocument(1, [], [], [], [], [], []);
        try
        {
            await Assert.ThrowsAsync<ManifestWriteException>(() => ManifestJson.WriteAsync(manifest, destination, CancellationToken.None));
            Assert.True(Directory.Exists(destination));
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task DirectoryInputFailsWithoutPathLeakage()
    {
        var root = Directory.CreateTempSubdirectory("logschema-input-");
        try
        {
            var exception = await Assert.ThrowsAsync<ManifestReadException>(() => ManifestJson.ReadAsync(root.FullName, CancellationToken.None));
            Assert.DoesNotContain(root.FullName, exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(true);
        }
    }
}
