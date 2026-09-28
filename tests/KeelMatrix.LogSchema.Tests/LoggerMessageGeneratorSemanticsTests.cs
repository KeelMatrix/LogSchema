namespace KeelMatrix.LogSchema.Tests;

public sealed class LoggerMessageGeneratorSemanticsTests
{
    [Fact]
    public void GeneratorVersionGateAcceptsOnlyTheGroundedPinnedAssembly()
    {
        Assert.True(LoggerMessageGeneratorSemantics.IsSupportedGeneratorVersion(new HashSet<string>(StringComparer.Ordinal)
        {
            LoggerMessageGeneratorSemantics.SupportedGeneratorAssemblyVersion
        }));
        Assert.True(LoggerMessageGeneratorSemantics.IsSupportedGeneratorVersion(new HashSet<string>(StringComparer.Ordinal)
        {
            LoggerMessageGeneratorSemantics.CurrentStableGeneratorAssemblyVersion
        }));
        Assert.False(LoggerMessageGeneratorSemantics.IsSupportedGeneratorVersion(new HashSet<string>(StringComparer.Ordinal)));
        Assert.False(LoggerMessageGeneratorSemantics.IsSupportedGeneratorVersion(new HashSet<string>(StringComparer.Ordinal) { "10.0.0.0" }));
        Assert.False(LoggerMessageGeneratorSemantics.IsSupportedGeneratorVersion(new HashSet<string>(StringComparer.Ordinal)
        {
            LoggerMessageGeneratorSemantics.SupportedGeneratorAssemblyVersion,
            LoggerMessageGeneratorSemantics.CurrentStableGeneratorAssemblyVersion
        }));
    }

    [Fact]
    public void GeneratorSupportRequiresTheExactResolvedAbstractionsPair()
    {
        var supported = new[]
        {
            (Abstractions: "10.0.1", Generator: "10.0.13.7005"),
            (Abstractions: "10.0.12", Generator: "10.0.14.42308")
        };
        foreach (var pair in supported)
        {
            Assert.True(LoggerMessageGeneratorSemantics.IsSupportedVersionPair(pair.Abstractions, pair.Generator));
        }

        var rejected = new[]
        {
            (Abstractions: "10.0.1", Generator: "10.0.14.42308"),
            (Abstractions: "10.0.12", Generator: "10.0.13.7005"),
            (Abstractions: "10.0.0", Generator: "10.0.13.2411"),
            (Abstractions: "unknown", Generator: "10.0.13.7005"),
            (Abstractions: "10.0.1", Generator: null as string),
            (Abstractions: null as string, Generator: "10.0.14.42308")
        };
        foreach (var pair in rejected)
        {
            Assert.False(LoggerMessageGeneratorSemantics.IsSupportedVersionPair(pair.Abstractions, pair.Generator));
        }
    }

    [Fact]
    public void FixedAndDynamicLevelSourcesShareRolesButDifferInStructuredState()
    {
        var fixedParameters = new[]
        {
            new ParameterContract("logger", "Microsoft.Extensions.Logging.ILogger", "None", "Logger"),
            new ParameterContract("level", "Microsoft.Extensions.Logging.LogLevel", "None", "LogLevel")
        };
        Assert.True(LoggerMessageGeneratorSemantics.TryCreatePersistedModel(fixedParameters, "logger", null, "Fixed", "Information", "level", out var fixedSemantics, out var fixedReason), fixedReason);
        Assert.True(fixedSemantics.IsStructuredState("level", dynamicLevel: false, []));

        var dynamicParameters = new[]
        {
            new ParameterContract("logger", "Microsoft.Extensions.Logging.ILogger", "None", "Logger"),
            new ParameterContract("firstLevel", "Microsoft.Extensions.Logging.LogLevel", "None", "LogLevel"),
            new ParameterContract("laterLevel", "Microsoft.Extensions.Logging.LogLevel", "None", "State")
        };
        Assert.True(LoggerMessageGeneratorSemantics.TryCreatePersistedModel(dynamicParameters, "logger", null, "Dynamic", "Dynamic", "firstLevel", out var dynamicSemantics, out var dynamicReason), dynamicReason);
        Assert.False(dynamicSemantics.IsStructuredState("firstLevel", dynamicLevel: true, []));
        Assert.True(dynamicSemantics.IsStructuredState("laterLevel", dynamicLevel: true, []));
    }

    [Theory]
    [InlineData("Fixed", "Information", null, true, false)]
    [InlineData("Fixed", "Information", "firstLevel", true, true)]
    [InlineData("Fixed", "Information", "laterLevel", false, true)]
    [InlineData("Fixed", "Information", "missingLevel", false, true)]
    [InlineData("Fixed", "Dynamic", null, false, false)]
    [InlineData("Fixed", "Dynamic", "firstLevel", false, true)]
    [InlineData("Dynamic", "Dynamic", "firstLevel", true, true)]
    [InlineData("Dynamic", "Dynamic", null, false, true)]
    [InlineData("Dynamic", "Dynamic", "laterLevel", false, true)]
    [InlineData("Dynamic", "Information", "firstLevel", false, true)]
    [InlineData("Dynamic", "Unknown", "firstLevel", false, true)]
    [InlineData("Unknown", "Information", null, false, false)]
    public void PersistedLevelCrossFieldMatrixFailsClosed(string source, string level, string? levelParameter, bool expected, bool hasLevelParameters)
    {
        var parameters = hasLevelParameters
            ? new[]
            {
                new ParameterContract("logger", "Microsoft.Extensions.Logging.ILogger", "None", "Logger"),
                new ParameterContract("firstLevel", "Microsoft.Extensions.Logging.LogLevel", "None", "LogLevel"),
                new ParameterContract("laterLevel", "Microsoft.Extensions.Logging.LogLevel", "None", "State")
            }
            : new[]
            {
                new ParameterContract("logger", "Microsoft.Extensions.Logging.ILogger", "None", "Logger")
            };

        var actual = LoggerMessageGeneratorSemantics.TryCreatePersistedModel(parameters, "logger", null, source, level, levelParameter, out _, out _);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void GeneratorTemplateRestrictionsAreSharedByExtractionAndManifestValidation()
    {
        var parameters = new[]
        {
            new ParameterContract("logger", "Microsoft.Extensions.Logging.ILogger", "None", "Logger"),
            new ParameterContract("level", "Microsoft.Extensions.Logging.LogLevel", "None", "LogLevel"),
            new ParameterContract("value", "int", "None", "State")
        };
        Assert.True(LoggerMessageGeneratorSemantics.TryCreatePersistedModel(parameters, "logger", null, "Fixed", "Information", "level", out var semantics, out var reason), reason);
        Assert.True(LoggerMessageGeneratorSemantics.TryValidateTemplate(semantics, dynamicLevel: false, "Event {level}", [new Placeholder("level", "level")], out reason), reason);
        Assert.False(LoggerMessageGeneratorSemantics.TryValidateTemplate(semantics, dynamicLevel: false, "Event {logger}", [new Placeholder("logger", "logger")], out reason));
        Assert.Contains("generator-special Logger", reason, StringComparison.Ordinal);
    }
}
