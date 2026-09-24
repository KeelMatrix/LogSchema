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
            Assert.Equal("ILogger,None", string.Join(',', events["DerivedException"].GetProperty("parameterForms").EnumerateArray().Select(item => item.GetString())));
            Assert.Contains("None:None:DerivedProblem", events["DerivedException"].GetProperty("identity").GetString(), StringComparison.Ordinal);
            Assert.Empty(events["DerivedException"].GetProperty("placeholders").EnumerateArray());
            Assert.Equal("ILogger,Exception", string.Join(',', events["ExactException"].GetProperty("parameterForms").EnumerateArray().Select(item => item.GetString())));
            Assert.Contains("None:Exception:System.Exception", events["ExactException"].GetProperty("identity").GetString(), StringComparison.Ordinal);
            Assert.Equal("ILogger,None", string.Join(',', events["OrdinaryCustom"].GetProperty("parameterForms").EnumerateArray().Select(item => item.GetString())));
            Assert.Contains("None:None:OrdinaryProblem", events["OrdinaryCustom"].GetProperty("identity").GetString(), StringComparison.Ordinal);
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

    [Fact]
    public async Task ShippingExtractorCapturesGeneratorEffectiveStructuredState()
    {
        var projectPath = FindRepositoryFile("tests", "PackageConsumerFixture", "PackageConsumerFixture.csproj");
        var outputPath = Path.Combine(Directory.CreateTempSubdirectory("logschema-state-model-").FullName, "logschema.json");
        try
        {
            using var output = new StringWriter();
            using var errors = new StringWriter();
            Assert.Equal(0, await CommandRunner.RunAsync(["capture", projectPath, "--output", outputPath, "--no-telemetry"], output, errors));

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
            var events = document.RootElement.GetProperty("events").EnumerateArray().ToDictionary(item => item.GetProperty("method").GetString()!, StringComparer.Ordinal);

            var absent = events["AbsentState"];
            Assert.Empty(absent.GetProperty("placeholders").EnumerateArray());
            Assert.Equal("customerId", absent.GetProperty("structuredState").EnumerateArray().Single().GetProperty("emittedName").GetString());
            Assert.Equal("State", absent.GetProperty("parameters").EnumerateArray().Last().GetProperty("role").GetString());

            var ordered = events["MethodOrder"];
            Assert.Equal(["Second", "First"], ordered.GetProperty("placeholders").EnumerateArray().Select(item => item.GetProperty("name").GetString()!).ToArray());
            Assert.Equal(["First", "Second"], ordered.GetProperty("structuredState").EnumerateArray().Select(item => item.GetProperty("emittedName").GetString()!).ToArray());

            Assert.Single(events["RepeatedPlaceholder"].GetProperty("structuredState").EnumerateArray());
            Assert.Equal("DisplayValue", events["PlaceholderCasing"].GetProperty("structuredState").EnumerateArray().Single().GetProperty("emittedName").GetString());
            Assert.Equal("value", events["PlaceholderRemoved"].GetProperty("structuredState").EnumerateArray().Single().GetProperty("emittedName").GetString());

            var exceptions = events["MultipleExceptions"];
            Assert.Equal("Exception", exceptions.GetProperty("parameters").EnumerateArray().ElementAt(1).GetProperty("role").GetString());
            Assert.Equal("State", exceptions.GetProperty("parameters").EnumerateArray().ElementAt(2).GetProperty("role").GetString());
            Assert.Equal("second", exceptions.GetProperty("structuredState").EnumerateArray().Single().GetProperty("emittedName").GetString());

            var loggers = events["MultipleLoggers"];
            Assert.Equal("Logger", loggers.GetProperty("parameters").EnumerateArray().ElementAt(0).GetProperty("role").GetString());
            Assert.Equal("State", loggers.GetProperty("parameters").EnumerateArray().ElementAt(1).GetProperty("role").GetString());
            Assert.Equal("firstLogger", loggers.GetProperty("loggerParameter").GetString());

            var levels = events["MultipleDynamicLevels"];
            Assert.Equal("DynamicLevel", levels.GetProperty("parameters").EnumerateArray().ElementAt(1).GetProperty("role").GetString());
            Assert.Equal("State", levels.GetProperty("parameters").EnumerateArray().ElementAt(2).GetProperty("role").GetString());
            Assert.Equal("firstLevel", levels.GetProperty("levelParameter").GetString());
            Assert.Equal("Fixed", events["FixedLevelParameter"].GetProperty("levelSource").GetString());

            var specialTemplate = events["SpecialExceptionInTemplate"];
            Assert.Equal("Exception", specialTemplate.GetProperty("parameters").EnumerateArray().ElementAt(1).GetProperty("role").GetString());
            Assert.Equal("exception", specialTemplate.GetProperty("structuredState").EnumerateArray().Single().GetProperty("emittedName").GetString());
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(outputPath)!, true);
        }
    }

    [Fact]
    public async Task ShippingExtractorAndDiffDetectStateAndRoleChanges()
    {
        var projectPath = FindRepositoryFile("tests", "PackageConsumerFixture", "PackageConsumerFixture.csproj");
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var sourcePath = Path.Combine(projectDirectory, "GeneratorStateLogging.cs");
        var original = await File.ReadAllTextAsync(sourcePath);
        var root = Directory.CreateTempSubdirectory("logschema-state-transitions-");
        var baselinePath = Path.Combine(root.FullName, "baseline.json");
        var currentPath = Path.Combine(root.FullName, "current.json");
        try
        {
            using var captureOutput = new StringWriter();
            using var captureErrors = new StringWriter();
            Assert.Equal(0, await CommandRunner.RunAsync(["capture", projectPath, "--output", baselinePath, "--no-telemetry"], captureOutput, captureErrors));

            await File.WriteAllTextAsync(sourcePath, original.Replace("int customerId", "int accountId", StringComparison.Ordinal));
            using var renameOutput = new StringWriter();
            using var renameErrors = new StringWriter();
            Assert.Equal(1, await CommandRunner.RunAsync(["check", projectPath, "--baseline", baselinePath, "--no-telemetry"], renameOutput, renameErrors));
            Assert.Contains("KMLOG102", renameOutput.ToString(), StringComparison.Ordinal);
            Assert.Contains("customerId", renameOutput.ToString(), StringComparison.Ordinal);
            Assert.Contains("accountId", renameOutput.ToString(), StringComparison.Ordinal);
            using var renamedCaptureOutput = new StringWriter();
            using var renamedCaptureErrors = new StringWriter();
            Assert.Equal(0, await CommandRunner.RunAsync(["capture", projectPath, "--output", currentPath, "--no-telemetry"], renamedCaptureOutput, renamedCaptureErrors));
            using var renameDiffOutput = new StringWriter();
            using var renameDiffErrors = new StringWriter();
            Assert.Equal(1, await CommandRunner.RunAsync(["diff", baselinePath, currentPath, "--no-telemetry"], renameDiffOutput, renameDiffErrors));
            Assert.Contains("KMLOG102", renameDiffOutput.ToString(), StringComparison.Ordinal);

            await File.WriteAllTextAsync(sourcePath, original.Replace("int customerId", "int customerId, string unreferenced", StringComparison.Ordinal));
            using var additionOutput = new StringWriter();
            using var additionErrors = new StringWriter();
            Assert.Equal(1, await CommandRunner.RunAsync(["check", projectPath, "--baseline", baselinePath, "--no-telemetry"], additionOutput, additionErrors));
            Assert.Contains("KMLOG002", additionOutput.ToString(), StringComparison.Ordinal);

            await File.WriteAllTextAsync(sourcePath, original.Replace("public static partial void AbsentState(ILogger logger, int customerId);", "public static partial void AbsentState(ILogger logger);", StringComparison.Ordinal));
            using var removalOutput = new StringWriter();
            using var removalErrors = new StringWriter();
            Assert.Equal(1, await CommandRunner.RunAsync(["check", projectPath, "--baseline", baselinePath, "--no-telemetry"], removalOutput, removalErrors));
            Assert.Contains("KMLOG002", removalOutput.ToString(), StringComparison.Ordinal);

            await File.WriteAllTextAsync(sourcePath, original.Replace("int first, int second", "int second, int first", StringComparison.Ordinal));
            using var orderOutput = new StringWriter();
            using var orderErrors = new StringWriter();
            Assert.Equal(1, await CommandRunner.RunAsync(["check", projectPath, "--baseline", baselinePath, "--no-telemetry"], orderOutput, orderErrors));
            Assert.Contains("KMLOG103", orderOutput.ToString(), StringComparison.Ordinal);

            await File.WriteAllTextAsync(sourcePath, original.Replace("RoleFlipProblem : Exception", "RoleFlipProblem", StringComparison.Ordinal));
            using var roleOutput = new StringWriter();
            using var roleErrors = new StringWriter();
            Assert.Equal(1, await CommandRunner.RunAsync(["check", projectPath, "--baseline", baselinePath, "--severity", "all", "--no-telemetry"], roleOutput, roleErrors));
            Assert.Contains("KMLOG105", roleOutput.ToString(), StringComparison.Ordinal);
            Assert.Contains("KMLOG104", roleOutput.ToString(), StringComparison.Ordinal);

            var ordinaryPath = Path.Combine(root.FullName, "ordinary.json");
            using var ordinaryCaptureOutput = new StringWriter();
            using var ordinaryCaptureErrors = new StringWriter();
            Assert.Equal(0, await CommandRunner.RunAsync(["capture", projectPath, "--output", ordinaryPath, "--no-telemetry"], ordinaryCaptureOutput, ordinaryCaptureErrors));
            await File.WriteAllTextAsync(sourcePath, original);
            using var reverseRoleOutput = new StringWriter();
            using var reverseRoleErrors = new StringWriter();
            Assert.Equal(1, await CommandRunner.RunAsync(["check", projectPath, "--baseline", ordinaryPath, "--severity", "all", "--no-telemetry"], reverseRoleOutput, reverseRoleErrors));
            Assert.Contains("KMLOG105", reverseRoleOutput.ToString(), StringComparison.Ordinal);

            await File.WriteAllTextAsync(sourcePath, original.Replace("Later logger {laterLogger}", "First logger {firstLogger}", StringComparison.Ordinal));
            using var loggerPlaceholderOutput = new StringWriter();
            using var loggerPlaceholderErrors = new StringWriter();
            Assert.Equal(3, await CommandRunner.RunAsync(["check", projectPath, "--baseline", baselinePath, "--no-telemetry"], loggerPlaceholderOutput, loggerPlaceholderErrors));
            var loggerPlaceholderText = loggerPlaceholderOutput.ToString() + loggerPlaceholderErrors;
            Assert.Contains("KMLOGP007", loggerPlaceholderText, StringComparison.Ordinal);
            Assert.Contains("generator-special Logger", loggerPlaceholderText, StringComparison.Ordinal);

            await File.WriteAllTextAsync(sourcePath, original.Replace("Later level {laterLevel}", "First level {firstLevel}", StringComparison.Ordinal));
            using var levelPlaceholderOutput = new StringWriter();
            using var levelPlaceholderErrors = new StringWriter();
            Assert.Equal(3, await CommandRunner.RunAsync(["check", projectPath, "--baseline", baselinePath, "--no-telemetry"], levelPlaceholderOutput, levelPlaceholderErrors));
            var levelPlaceholderText = levelPlaceholderOutput.ToString() + levelPlaceholderErrors;
            Assert.Contains("KMLOGP007", levelPlaceholderText, StringComparison.Ordinal);
            Assert.Contains("generator-special DynamicLevel", levelPlaceholderText, StringComparison.Ordinal);
        }
        finally
        {
            await File.WriteAllTextAsync(sourcePath, original);
            root.Delete(true);
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
