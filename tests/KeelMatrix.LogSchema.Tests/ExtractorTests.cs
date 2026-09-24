using System.Text.Json;
using KeelMatrix.LogSchema;

namespace KeelMatrix.LogSchema.Tests;

public sealed class ExtractorTests
{
    [Fact]
    public async Task ShippingExtractorCapturesGeneratorEffectiveDefaultsAndDynamicLevel()
    {
        var projectPath = FindRepositoryFile("tests", "PackageConsumerFixture", "PackageConsumerFixture.csproj");
        var outputPath = Path.Combine(Directory.CreateTempSubdirectory("logschema-extractor-").FullName, "logschema.json");
        try
        {
            using var output = new StringWriter();
            using var errors = new StringWriter();
            var exitCode = await CommandRunner.RunAsync(["capture", projectPath, "--output", outputPath, "--no-telemetry"], output, errors);

            Assert.Equal(0, exitCode);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
            var events = document.RootElement.GetProperty("events").EnumerateArray().ToDictionary(item => item.GetProperty("method").GetString()!, StringComparer.Ordinal);

            var omittedEventId = events["OmittedEventId"];
            Assert.Equal("OmittedEventId", omittedEventId.GetProperty("eventName").GetString());
            Assert.Equal(NonRandomizedHashCode("OmittedEventId"), omittedEventId.GetProperty("eventId").GetInt32());
            Assert.Equal("Information", omittedEventId.GetProperty("level").GetString());
            Assert.Equal(1001, events["Processed"].GetProperty("eventId").GetInt32());
            Assert.Equal("Dynamic", events["DynamicLevel"].GetProperty("level").GetString());
            Assert.Equal("None", events["FixedNone"].GetProperty("level").GetString());
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(outputPath)!, true);
        }
    }

    [Fact]
    public async Task ShippingExtractorPreservesDynamicToFixedLevelTransition()
    {
        var projectPath = FindRepositoryFile("tests", "PackageConsumerFixture", "PackageConsumerFixture.csproj");
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var sourcePath = Path.Combine(projectDirectory, "ConsumerLogging.cs");
        var original = await File.ReadAllTextAsync(sourcePath);
        var outputPath = Path.Combine(Directory.CreateTempSubdirectory("logschema-transition-").FullName, "logschema.json");
        try
        {
            using var captureOutput = new StringWriter();
            using var captureErrors = new StringWriter();
            Assert.Equal(0, await CommandRunner.RunAsync(["capture", projectPath, "--output", outputPath, "--no-telemetry"], captureOutput, captureErrors));

            await File.WriteAllTextAsync(sourcePath, original.Replace("[LoggerMessage(Message = \"Dynamic level {Value}\")]", "[LoggerMessage(Level = LogLevel.None, Message = \"Dynamic level {Value}\")]", StringComparison.Ordinal));
            using var checkOutput = new StringWriter();
            using var checkErrors = new StringWriter();
            var exitCode = await CommandRunner.RunAsync(["check", projectPath, "--baseline", outputPath, "--severity", "warning", "--no-telemetry"], checkOutput, checkErrors);

            Assert.Equal(1, exitCode);
            Assert.Contains("KMLOG201", checkOutput.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            await File.WriteAllTextAsync(sourcePath, original);
            Directory.Delete(Path.GetDirectoryName(outputPath)!, true);
        }
    }

    private static int NonRandomizedHashCode(string value)
    {
        uint result = 2166136261u;
        foreach (var character in value)
        {
            result = (character ^ result) * 16777619;
        }

        var hash = (int)result;
        return hash == int.MinValue ? 0 : Math.Abs(hash);
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
