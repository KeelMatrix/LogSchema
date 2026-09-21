using System.Text;
using KeelMatrix.LogSchema;

namespace KeelMatrix.LogSchema.Tests;

public sealed class ManifestJsonTests
{
    [Fact]
    public async Task SerializationIsCanonicalUtf8AndPathIndependent()
    {
        var manifest = new ManifestDocument(1, [new ProjectIdentity("P|net8.0", "P", "P", "net8.0")], [new EventContract("P|net8.0", "P.Logging.Event`0(Microsoft.Extensions.Logging.ILogger)", "P.Logging", "Event", 0, ["None"], 10, "Event", "Information", "Событие {Идентификатор}", [new Placeholder("Идентификатор", "Идентификатор")], ["ILogger"], new SourceLocation("src/Logging.cs", 4, "source"))], [], [], [], []);
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
        var malformed = Path.Combine(root, "malformed.json");
        await File.WriteAllTextAsync(future, "{\"schemaVersion\":99}");
        await File.WriteAllTextAsync(malformed, "{\"schemaVersion\":1");
        try
        {
            await Assert.ThrowsAsync<ManifestReadException>(() => ManifestJson.ReadAsync(future, CancellationToken.None));
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
}
