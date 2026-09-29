using System.Diagnostics;
using System.Text.Json;
using KeelMatrix.LogSchema;

namespace KeelMatrix.LogSchema.Tests;

public sealed class ExtractorTests
{
    [Fact]
    public async Task ImplicitAndExplicitFrameworkSelectionProduceIdenticalIdentity()
    {
        var projectPath = FindRepositoryFile("tests", "PackageConsumerFixture", "PackageConsumerFixture.csproj");
        var root = Directory.CreateTempSubdirectory("logschema-tfm-identity-");
        var implicitPath = Path.Combine(root.FullName, "implicit.json");
        var explicitPath = Path.Combine(root.FullName, "explicit.json");
        try
        {
            using var implicitOutput = new StringWriter();
            using var implicitErrors = new StringWriter();
            Assert.Equal(0, await CommandRunner.RunAsync(["capture", projectPath, "--output", implicitPath, "--no-telemetry"], implicitOutput, implicitErrors));
            using var explicitOutput = new StringWriter();
            using var explicitErrors = new StringWriter();
            Assert.Equal(0, await CommandRunner.RunAsync(["capture", projectPath, "--tfm", "net8.0", "--output", explicitPath, "--no-telemetry"], explicitOutput, explicitErrors));
            Assert.Equal(await File.ReadAllTextAsync(implicitPath), await File.ReadAllTextAsync(explicitPath));
            Assert.Contains("PackageConsumerFixture|net8.0", await File.ReadAllTextAsync(implicitPath), StringComparison.Ordinal);

            using var invalidOutput = new StringWriter();
            using var invalidErrors = new StringWriter();
            Assert.Equal(3, await CommandRunner.RunAsync(["capture", projectPath, "--tfm", "net9.0", "--output", Path.Combine(root.FullName, "invalid.json"), "--format", "json", "--no-telemetry"], invalidOutput, invalidErrors));
            Assert.DoesNotContain("at KeelMatrix", invalidOutput.ToString(), StringComparison.Ordinal);
            Assert.False(JsonDocument.Parse(invalidOutput.ToString()).RootElement.GetProperty("coverageComplete").GetBoolean());
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task MultiTargetSelectionRequiresDeclaredFrameworkAndPreservesCanonicalIdentity()
    {
        var root = Directory.CreateTempSubdirectory("logschema-multitarget-");
        var projectPath = Path.Combine(root.FullName, "MultiTarget.csproj");
        var implicitPath = Path.Combine(root.FullName, "implicit.json");
        var explicitPath = Path.Combine(root.FullName, "explicit.json");
        await File.WriteAllTextAsync(projectPath, """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFrameworks>net8.0;netstandard2.0</TargetFrameworks>
            <AssemblyName>MultiTarget</AssemblyName>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.1" />
          </ItemGroup>
        </Project>
        """);
        await File.WriteAllTextAsync(Path.Combine(root.FullName, "Logging.cs"), """
        using Microsoft.Extensions.Logging;
        public static partial class MultiTargetLogging
        {
            [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Value {value}")]
            public static partial void Event(ILogger logger, int value);
        }
        """);
        try
        {
            await RestoreAsync(projectPath);

            using var implicitOutput = new StringWriter();
            using var implicitErrors = new StringWriter();
            Assert.Equal(3, await CommandRunner.RunAsync(["capture", projectPath, "--output", implicitPath, "--format", "json", "--no-telemetry"], implicitOutput, implicitErrors));
            Assert.Contains("multiple frameworks", implicitOutput.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.False(JsonDocument.Parse(implicitOutput.ToString()).RootElement.GetProperty("coverageComplete").GetBoolean());

            using var explicitOutput = new StringWriter();
            using var explicitErrors = new StringWriter();
            Assert.Equal(0, await CommandRunner.RunAsync(["capture", projectPath, "--tfm", "net8.0", "--output", explicitPath, "--no-telemetry"], explicitOutput, explicitErrors));
            var explicitManifest = await File.ReadAllTextAsync(explicitPath);
            Assert.Contains("MultiTarget|net8.0", explicitManifest, StringComparison.Ordinal);

            using var invalidOutput = new StringWriter();
            using var invalidErrors = new StringWriter();
            Assert.Equal(3, await CommandRunner.RunAsync(["capture", projectPath, "--tfm", "net9.0", "--output", Path.Combine(root.FullName, "invalid.json"), "--format", "json", "--no-telemetry"], invalidOutput, invalidErrors));
            Assert.Contains("not declared", invalidOutput.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.False(JsonDocument.Parse(invalidOutput.ToString()).RootElement.GetProperty("coverageComplete").GetBoolean());
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Theory]
    [InlineData("escaped {{literal}}", true, "")]
    [InlineData("odd {{{Value}}}", true, "Value")]
    [InlineData("even {{{{Value}}}}", true, "")]
    [InlineData("spaces { Value ,10:000 }", true, "Value")]
    [InlineData("at {@Value}", true, "@Value")]
    [InlineData("casing {CustomerID}", true, "CustomerID")]
    [InlineData("unicode {Идентификатор}", true, "Идентификатор")]
    [InlineData("stray closing }", false, "")]
    [InlineData("unmatched opening {Value", false, "")]
    [InlineData("wrong brace {Value } tail }", false, "")]
    public void TemplateParserMatchesGeneratorBraceAndNameSemantics(string message, bool expectedSuccess, string expectedName)
    {
        var success = LogSchemaExtractor.TryReadPlaceholders(message, out var placeholders, out _);

        Assert.Equal(expectedSuccess, success);
        if (expectedSuccess)
        {
            Assert.Equal(expectedName.Length == 0 ? 0 : 1, placeholders.Count);
            if (expectedName.Length > 0)
            {
                Assert.Equal(expectedName, placeholders[0].Name);
            }
        }
    }

    [Fact]
    public void TemplateParserPreservesFormatTokenAndRejectsEmptyNames()
    {
        Assert.True(LogSchemaExtractor.TryReadPlaceholders("{Value,10:000}", out var placeholders, out var reason), reason);
        var placeholder = Assert.Single(placeholders);
        Assert.Equal("Value", placeholder.Name);
        Assert.Equal("Value,10:000", placeholder.Token);

        Assert.False(LogSchemaExtractor.TryReadPlaceholders("{} { }", out _, out reason));
        Assert.Contains("empty placeholder", reason, StringComparison.Ordinal);
    }

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
            Assert.All(events.Values, @event =>
            {
                var source = @event.GetProperty("source").GetProperty("file").GetString()!;
                Assert.StartsWith("project/", source, StringComparison.Ordinal);
                Assert.DoesNotContain(Path.GetDirectoryName(projectPath)!, source, StringComparison.OrdinalIgnoreCase);
            });

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
            Assert.Equal("LogLevel", levels.GetProperty("parameters").EnumerateArray().ElementAt(1).GetProperty("role").GetString());
            Assert.Equal("State", levels.GetProperty("parameters").EnumerateArray().ElementAt(2).GetProperty("role").GetString());
            Assert.Equal("firstLevel", levels.GetProperty("levelParameter").GetString());
            Assert.Equal("Fixed", events["FixedLevelParameter"].GetProperty("levelSource").GetString());

            var fixedLevelAbsent = events["FixedLevelParameterAbsentFromTemplate"];
            Assert.Equal("LogLevel", fixedLevelAbsent.GetProperty("parameters").EnumerateArray().ElementAt(1).GetProperty("role").GetString());
            Assert.Equal("level", fixedLevelAbsent.GetProperty("structuredState").EnumerateArray().Single().GetProperty("emittedName").GetString());
            Assert.Equal("level", fixedLevelAbsent.GetProperty("levelParameter").GetString());

            var fixedLevels = events["MultipleFixedLevels"];
            Assert.Equal("LogLevel", fixedLevels.GetProperty("parameters").EnumerateArray().ElementAt(1).GetProperty("role").GetString());
            Assert.Equal("State", fixedLevels.GetProperty("parameters").EnumerateArray().ElementAt(2).GetProperty("role").GetString());
            Assert.Equal(["firstLevel", "laterLevel"], fixedLevels.GetProperty("structuredState").EnumerateArray().Select(item => item.GetProperty("emittedName").GetString()!).ToArray());

            var customLogger = events["CustomLoggerFirst"];
            Assert.Equal("Logger", customLogger.GetProperty("parameters").EnumerateArray().ElementAt(0).GetProperty("role").GetString());
            Assert.Equal("State", customLogger.GetProperty("parameters").EnumerateArray().ElementAt(1).GetProperty("role").GetString());
            Assert.Equal("first", customLogger.GetProperty("loggerParameter").GetString());
            Assert.Equal("second", customLogger.GetProperty("structuredState").EnumerateArray().Single().GetProperty("emittedName").GetString());

            var overlapping = events["OverlappingSpecialRoles"];
            Assert.Equal("Logger|Exception", overlapping.GetProperty("parameters").EnumerateArray().ElementAt(0).GetProperty("role").GetString());
            Assert.Equal("value", overlapping.GetProperty("loggerParameter").GetString());
            Assert.Equal("value", overlapping.GetProperty("exceptionParameter").GetString());
            Assert.Empty(overlapping.GetProperty("structuredState").EnumerateArray());

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
    public async Task ShippingExtractorPreservesDistinctLinkedAndDotPrefixedSourceProvenance()
    {
        var projectPath = FindRepositoryFile("tests", "ProvenanceFixture", "ProvenanceFixture.csproj");
        var outputPath = Path.Combine(Directory.CreateTempSubdirectory("logschema-provenance-fixture-").FullName, "logschema.json");
        try
        {
            using var output = new StringWriter();
            using var errors = new StringWriter();
            Assert.Equal(0, await CommandRunner.RunAsync(["capture", projectPath, "--output", outputPath, "--no-telemetry"], output, errors));

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
            var events = document.RootElement.GetProperty("events").EnumerateArray().ToDictionary(item => item.GetProperty("method").GetString()!, StringComparer.Ordinal);
            Assert.Equal("project/Shared/Logging.cs", events["InProject"].GetProperty("source").GetProperty("file").GetString());
            Assert.Equal("project/.hidden/Logging.cs", events["DotPrefixed"].GetProperty("source").GetProperty("file").GetString());
            Assert.Equal("external/up-1/Shared/Logging.cs", events["Linked"].GetProperty("source").GetProperty("file").GetString());
            Assert.NotEqual(
                events["InProject"].GetProperty("source").GetProperty("file").GetString(),
                events["Linked"].GetProperty("source").GetProperty("file").GetString());
            Assert.DoesNotContain(Path.GetDirectoryName(projectPath)!, await File.ReadAllTextAsync(outputPath), StringComparison.OrdinalIgnoreCase);
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

            await File.WriteAllTextAsync(sourcePath, original.Replace("FixedLevelParameterAbsentFromTemplate(ILogger logger, LogLevel level)", "FixedLevelParameterAbsentFromTemplate(ILogger logger, LogLevel severity)", StringComparison.Ordinal));
            using var fixedLevelRenameOutput = new StringWriter();
            using var fixedLevelRenameErrors = new StringWriter();
            Assert.Equal(1, await CommandRunner.RunAsync(["check", projectPath, "--baseline", baselinePath, "--no-telemetry"], fixedLevelRenameOutput, fixedLevelRenameErrors));
            Assert.Contains("KMLOG102", fixedLevelRenameOutput.ToString(), StringComparison.Ordinal);

            await File.WriteAllTextAsync(sourcePath, original.Replace("Custom logger {second}", "Custom logger {renamedSecond}", StringComparison.Ordinal).Replace("CustomLoggerFirst(CustomLogger first, ILogger second)", "CustomLoggerFirst(CustomLogger first, ILogger renamedSecond)", StringComparison.Ordinal));
            using var customLoggerRenameOutput = new StringWriter();
            using var customLoggerRenameErrors = new StringWriter();
            Assert.Equal(1, await CommandRunner.RunAsync(["check", projectPath, "--baseline", baselinePath, "--no-telemetry"], customLoggerRenameOutput, customLoggerRenameErrors));
            Assert.Contains("KMLOG102", customLoggerRenameOutput.ToString(), StringComparison.Ordinal);

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

    private static async Task RestoreAsync(string projectPath)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"restore \"{projectPath}\" --configfile \"{FindRepositoryFile("NuGet.config")}\" --nologo",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        Assert.NotNull(process);
        await process!.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync());
    }
}
