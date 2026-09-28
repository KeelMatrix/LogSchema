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
        Assert.True(LoggerMessageGeneratorSemantics.TryValidateTemplate(semantics, dynamicLevel: false, [new Placeholder("level", "level")], out reason), reason);
        Assert.False(LoggerMessageGeneratorSemantics.TryValidateTemplate(semantics, dynamicLevel: false, [new Placeholder("logger", "logger")], out reason));
        Assert.Contains("generator-special Logger", reason, StringComparison.Ordinal);
    }
}
