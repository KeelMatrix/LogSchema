using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KeelMatrix.LogSchema;

namespace KeelMatrix.LogSchema.Tests;

public sealed class ManifestJsonTests
{
    [Theory]
    [InlineData("Microsoft.Extensions.Logging.ILogger", "ILogger")]
    [InlineData("Microsoft.Extensions.Logging.ILogger<P.Category>", "ILogger")]
    [InlineData("Microsoft.Extensions.Logging.ILogger<P.Category,P.Other>", "None")]
    [InlineData("Microsoft.Extensions.Logging.ILogger<P.Category>.Nested<P.Other>", "None")]
    [InlineData("Microsoft.Extensions.Logging.LogLevel", "LogLevel")]
    [InlineData("System.Exception", "Exception")]
    [InlineData("P.DerivedProblem", "None")]
    [InlineData("P.OrdinaryProblem", "None")]
    [InlineData("string", "None")]
    public void RequiredParameterFormIsAFunctionOfCanonicalDeclaredType(string type, string expected) =>
        Assert.Equal(expected, ManifestJson.GetRequiredParameterForm(type));

    [Fact]
    public async Task SerializationIsCanonicalUtf8AndPathIndependent()
    {
        var manifest = new ManifestDocument(1, [new ProjectIdentity("P|net8.0", "P", "P", "net8.0")], [new EventContract("P|net8.0", "P.Logging.Event`0(None:ILogger:Microsoft.Extensions.Logging.ILogger,None:None:string)", "P.Logging", "Event", 0, ["None", "None"], 10, "Event", "Information", "Событие {Идентификатор}", [new Placeholder("Идентификатор", "Идентификатор")], ["ILogger", "None"], new SourceLocation("src/Logging.cs", 4, "source"), [new ParameterContract("logger", "Microsoft.Extensions.Logging.ILogger", "None", "Logger"), new ParameterContract("Идентификатор", "string", "None", "State")], [new StructuredStateProperty("Идентификатор", "Идентификатор")], "logger", null, "Fixed", null)], [], [], [], []);
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
        const int eventCount = 1_500;
        var root = Directory.CreateTempSubdirectory("logschema-large-");
        var path = Path.Combine(root.FullName, "large.json");
        var events = Enumerable.Range(0, eventCount)
            .Select(index => new EventContract(
                "Large|net8.0",
                $"Large.Logging.Event{index}`0(None:ILogger:Microsoft.Extensions.Logging.ILogger,None:None:int)",
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
                new SourceLocation($"Logging/Event{index}.cs", index + 1, "source"),
                [new ParameterContract("logger", "Microsoft.Extensions.Logging.ILogger", "None", "Logger"), new ParameterContract($"value{index}", "int", "None", "State")],
                [new StructuredStateProperty($"value{index}", $"Value{index}")],
                "logger",
                null,
                "Fixed",
                null))
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
            "identity": "Logging.Event`0(None:ILogger:Microsoft.Extensions.Logging.ILogger,None:None:string,None:None:string,None:None:string)",
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
            "identity": "Logging.Event`0(None:ILogger:Microsoft.Extensions.Logging.ILogger,None:None:string,None:None:string,None:None:string)",
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
            "parameters": [
              { "name": "logger", "type": "Microsoft.Extensions.Logging.ILogger", "refKind": "None", "role": "Logger" },
              { "name": "one", "type": "string", "refKind": "None", "role": "State" },
              { "name": "two", "type": "string", "refKind": "None", "role": "State" },
              { "name": "three", "type": "string", "refKind": "None", "role": "State" }
            ],
            "structuredState": [
              { "parameterName": "one", "emittedName": "One" },
              { "parameterName": "two", "emittedName": "Two" },
              { "parameterName": "three", "emittedName": "Three" }
            ],
            "loggerParameter": "logger",
            "exceptionParameter": null,
            "levelSource": "Fixed",
            "levelParameter": null,
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
            case "malformedIdentity": eventNode["identity"] = "Logging.Event`0(None:ILogger:Microsoft.Extensions.Logging.ILogger"; break;
            case "invalidTypeSyntax": eventNode["identity"] = "Logging.Event`0(None:ILogger:Microsoft.Extensions.Logging.ILogger,None:None:string,None:None:string,None:None:???)"; break;
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
    public async Task SemanticFormDecisionTableCoversEveryFormAtEveryPosition()
    {
        var root = Directory.CreateTempSubdirectory("logschema-form-totality-");
        var manifest = JsonNode.Parse("""
        {
          "schemaVersion": 1,
          "projects": [{ "key": "P|net8.0", "name": "P", "assembly": "P", "targetFramework": "net8.0" }],
          "events": [{
            "projectKey": "P|net8.0",
            "identity": "P.Logging.Event`0(None:ILogger:Microsoft.Extensions.Logging.ILogger,None:LogLevel:Microsoft.Extensions.Logging.LogLevel,None:Exception:System.Exception,None:None:P.DerivedProblem,None:None:P.OrdinaryProblem,None:None:string)",
            "containingType": "P.Logging",
            "method": "Event",
            "genericArity": 0,
            "parameterRefKinds": ["None", "None", "None", "None", "None", "None"],
            "eventId": 1,
            "eventName": "Event",
            "level": "Dynamic",
            "message": "Event",
            "placeholders": [],
            "parameterForms": ["ILogger", "LogLevel", "Exception", "None", "None", "None"],
            "parameters": [
              { "name": "logger", "type": "Microsoft.Extensions.Logging.ILogger", "refKind": "None", "role": "Logger" },
              { "name": "level", "type": "Microsoft.Extensions.Logging.LogLevel", "refKind": "None", "role": "DynamicLevel" },
              { "name": "exception", "type": "System.Exception", "refKind": "None", "role": "Exception" },
              { "name": "derived", "type": "P.DerivedProblem", "refKind": "None", "role": "State" },
              { "name": "ordinary", "type": "P.OrdinaryProblem", "refKind": "None", "role": "State" },
              { "name": "value", "type": "string", "refKind": "None", "role": "State" }
            ],
            "structuredState": [
              { "parameterName": "derived", "emittedName": "derived" },
              { "parameterName": "ordinary", "emittedName": "ordinary" },
              { "parameterName": "value", "emittedName": "value" }
            ],
            "loggerParameter": "logger",
            "exceptionParameter": "exception",
            "levelSource": "Dynamic",
            "levelParameter": "level",
            "source": { "file": "Logging.cs", "line": 1, "kind": "source" }
          }],
          "unsupported": [],
          "analysisIssues": [],
          "compilationDiagnosticKinds": [],
          "workspaceDiagnosticKinds": []
        }
        """)!;
        var expectedForms = new[] { "ILogger", "LogLevel", "Exception", "None", "None", "None" };
        var allForms = new[] { "None", "Exception", "ILogger", "LogLevel" };

        try
        {
            for (var position = 0; position < expectedForms.Length; position++)
            {
                foreach (var candidate in allForms)
                {
                    var candidateManifest = JsonNode.Parse(manifest.ToJsonString())!;
                    candidateManifest["events"]![0]!["parameterForms"]![position] = candidate;
                    var path = Path.Combine(root.FullName, $"position-{position}-{candidate}.json");
                    await File.WriteAllTextAsync(path, candidateManifest.ToJsonString());

                    if (candidate == expectedForms[position])
                    {
                        _ = await ManifestJson.ReadAsync(path, CancellationToken.None);
                    }
                    else
                    {
                        await Assert.ThrowsAsync<ManifestReadException>(() => ManifestJson.ReadAsync(path, CancellationToken.None));
                    }
                }
            }
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task ReaderRecomputesFormsForEveryAcceptedGeneratedManifest()
    {
        const int cases = 64;
        var random = new Random(0x4C534348);
        var root = Directory.CreateTempSubdirectory("logschema-form-fuzz-");
        var validPath = Path.Combine(root.FullName, "valid.json");
        var forgedPath = Path.Combine(root.FullName, "forged.json");
        var embeddedPath = Path.Combine(root.FullName, "embedded.json");
        var redundantPath = Path.Combine(root.FullName, "redundant.json");
        var typePool = new[]
        {
            new TypePoolEntry("Microsoft.Extensions.Logging.ILogger", "ILogger", true),
            new TypePoolEntry("Microsoft.Extensions.Logging.ILogger<P.OrdinaryProblem>", "ILogger", true),
            new TypePoolEntry("Microsoft.Extensions.Logging.LogLevel", "LogLevel", true),
            new TypePoolEntry("System.Exception", "Exception", true),
            new TypePoolEntry("P.DerivedProblem", "None", true),
            new TypePoolEntry("P.OrdinaryProblem", "None", true),
            new TypePoolEntry("string", "None", true),
            new TypePoolEntry("global::System.Exception", "None", false),
            new TypePoolEntry("System.@Exception", "None", false),
            new TypePoolEntry("Microsoft.Extensions.Logging.@LogLevel", "None", false),
            new TypePoolEntry("Microsoft.Extensions.Logging.@ILogger<P.OrdinaryProblem>", "None", false),
            new TypePoolEntry("Microsoft.Extensions.Logging.ILogger <P.OrdinaryProblem>", "None", false),
            new TypePoolEntry("Microsoft.Extensions.Logging.ILogger<P.OrdinaryProblem,P.Other>", "ILogger", false),
            new TypePoolEntry("Microsoft.Extensions.Logging.ILogger<P.OrdinaryProblem>.Nested<P.Other>", "ILogger", false)
        };
        var canonicalTypePool = typePool.Where(entry => entry.IsCanonical).ToArray();
        var allForms = new[] { "None", "Exception", "ILogger", "LogLevel" };

        try
        {
            for (var iteration = 0; iteration < cases; iteration++)
            {
                var parameterCount = random.Next(1, 9);
                var types = Enumerable.Range(0, parameterCount).Select(_ => canonicalTypePool[random.Next(canonicalTypePool.Length)].Text).ToArray();
                types[random.Next(parameterCount)] = random.Next(2) == 0
                    ? "Microsoft.Extensions.Logging.ILogger"
                    : "Microsoft.Extensions.Logging.ILogger<P.OrdinaryProblem>";
                var requiredForms = types.Select(ManifestJson.GetRequiredParameterForm).ToArray();
                var method = "Generated" + iteration.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var valid = FormManifest(method, types, requiredForms, requiredForms);

                await ManifestJson.WriteAsync(valid, validPath, CancellationToken.None);
                var accepted = await ManifestJson.ReadAsync(validPath, CancellationToken.None);
                var acceptedEvent = Assert.Single(accepted.Events);
                Assert.Equal(requiredForms, acceptedEvent.ParameterForms);
                Assert.Equal(requiredForms, types.Select(ManifestJson.GetRequiredParameterForm));
                Assert.Equal(Assert.Single(valid.Events).Identity, acceptedEvent.Identity);

                for (var position = 0; position < parameterCount; position++)
                {
                    var suppliedForms = requiredForms.ToArray();
                    suppliedForms[position] = allForms.First(form => form != requiredForms[position]);

                    var coordinatedForgery = FormManifest(method, types, suppliedForms, suppliedForms);
                    await File.WriteAllTextAsync(forgedPath, JsonSerializer.Serialize(coordinatedForgery));
                    await Assert.ThrowsAsync<ManifestReadException>(() => ManifestJson.ReadAsync(forgedPath, CancellationToken.None));

                    var embeddedForgery = FormManifest(method, types, suppliedForms, requiredForms);
                    await File.WriteAllTextAsync(embeddedPath, JsonSerializer.Serialize(embeddedForgery));
                    await Assert.ThrowsAsync<ManifestReadException>(() => ManifestJson.ReadAsync(embeddedPath, CancellationToken.None));

                    var redundantForgery = FormManifest(method, types, requiredForms, suppliedForms);
                    await File.WriteAllTextAsync(redundantPath, JsonSerializer.Serialize(redundantForgery));
                    await Assert.ThrowsAsync<ManifestReadException>(() => ManifestJson.ReadAsync(redundantPath, CancellationToken.None));
                }
            }

            foreach (var (mutation, index) in typePool.Where(entry => !entry.IsCanonical).Select((entry, index) => (entry, index)))
            {
                var invalid = FormManifest("Mutated" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), ["Microsoft.Extensions.Logging.ILogger", mutation.Text], ["ILogger", mutation.ExpectedForm], ["ILogger", mutation.ExpectedForm]);
                var mutationPath = Path.Combine(root.FullName, "mutation-" + Guid.NewGuid().ToString("N") + ".json");
                await File.WriteAllTextAsync(mutationPath, JsonSerializer.Serialize(invalid));
                await Assert.ThrowsAsync<ManifestReadException>(() => ManifestJson.ReadAsync(mutationPath, CancellationToken.None));
            }
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Theory]
    [InlineData("P.Logging.Event`0(None:None:Microsoft.Extensions.Logging.ILogger)", new[] { "None" })]
    [InlineData("P.Logging.Event`0(None:ILogger:Microsoft.Extensions.Logging.ILogger,None:None:Microsoft.Extensions.Logging.LogLevel)", new[] { "ILogger", "None" })]
    [InlineData("P.Logging.Event`0(None:ILogger:Microsoft.Extensions.Logging.ILogger,None:None:System.Exception)", new[] { "ILogger", "None" })]
    [InlineData("P.Logging.Event`0(None:ILogger:Microsoft.Extensions.Logging.ILogger,None:Exception:P.DerivedProblem)", new[] { "ILogger", "Exception" })]
    [InlineData("P.Logging.Event`0(None:ILogger:Microsoft.Extensions.Logging.ILogger,None:Exception:string)", new[] { "ILogger", "Exception" })]
    [InlineData("P.Logging.Event`0(None:ILogger:Microsoft.Extensions.Logging.ILogger,None:ILogger:P.OrdinaryProblem)", new[] { "ILogger", "ILogger" })]
    [InlineData("P.Logging.Event`0(None:ILogger:Microsoft.Extensions.Logging.ILogger,None:LogLevel:P.OrdinaryProblem)", new[] { "ILogger", "LogLevel" })]
    public async Task ContradictorySemanticTagsForKnownTypeClassesFailClosed(string identity, string[] forms)
    {
        var root = Directory.CreateTempSubdirectory("logschema-form-tag-");
        var path = Path.Combine(root.FullName, "manifest.json");
        var manifest = new ManifestDocument(
            1,
            [new ProjectIdentity("P|net8.0", "P", "P", "net8.0")],
            [SyntheticEvent(identity, "Event", forms)],
            [],
            [],
            [],
            []);

        try
        {
            await Assert.ThrowsAsync<ManifestWriteException>(() => ManifestJson.WriteAsync(manifest, path, CancellationToken.None));
        }
        finally
        {
            root.Delete(true);
        }
    }

    private static ManifestDocument FormManifest(string method, string[] types, string[] embeddedForms, string[] parameterForms)
    {
        var identity = "P.Logging." + method + "`0(" + string.Join(",", types.Select((type, index) => "None:" + embeddedForms[index] + ":" + type)) + ")";
        return new ManifestDocument(
            1,
            [new ProjectIdentity("P|net8.0", "P", "P", "net8.0")],
            [SyntheticEvent(identity, method, parameterForms, types)],
            [],
            [],
            [],
            []);
    }

    private static EventContract SyntheticEvent(string identity, string method, string[] parameterForms, string[]? types = null, string message = "Event")
    {
        types ??= Enumerable.Repeat("string", parameterForms.Length).ToArray();
        var loggerAssigned = false;
        var exceptionAssigned = false;
        var parameters = types.Select((type, index) =>
        {
            var form = parameterForms[index];
            var role = !loggerAssigned && form == "ILogger" ? "Logger" : !exceptionAssigned && form == "Exception" ? "Exception" : "State";
            loggerAssigned |= role == "Logger";
            exceptionAssigned |= role == "Exception";
            return new ParameterContract("parameter" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), type, "None", role);
        }).ToArray();
        var loggerParameter = parameters.FirstOrDefault(parameter => parameter.Role == "Logger")?.Name ?? string.Empty;
        var exceptionParameter = parameters.FirstOrDefault(parameter => parameter.Role == "Exception")?.Name;
        var structuredState = parameters.Where(parameter => parameter.Role == "State").Select(parameter => new StructuredStateProperty(parameter.Name, parameter.Name)).ToArray();
        return new EventContract("P|net8.0", identity, "P.Logging", method, 0, Enumerable.Repeat("None", types.Length).ToArray(), 1, method, "Information", message, [], parameterForms, new SourceLocation("Logging.cs", 1, "source"), parameters, structuredState, loggerParameter, exceptionParameter, "Fixed", null);
    }

    private sealed record TypePoolEntry(string Text, string ExpectedForm, bool IsCanonical);

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
            [SyntheticEvent("P.Event`0(None:ILogger:Microsoft.Extensions.Logging.ILogger)", "Event", ["ILogger"], ["Microsoft.Extensions.Logging.ILogger"], huge)],
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
