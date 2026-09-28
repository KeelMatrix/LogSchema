using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace KeelMatrix.LogSchema;

[Flags]
internal enum GeneratorParameterRoles
{
    None = 0,
    Logger = 1,
    Exception = 2,
    LogLevel = 4
}

internal sealed record GeneratorParameterSemantics(string Name, GeneratorParameterRoles Roles)
{
    internal string Role => LoggerMessageGeneratorSemantics.FormatRole(Roles);
}

internal sealed record LoggerMessageMethodSemantics(
    IReadOnlyList<GeneratorParameterSemantics> Parameters,
    string LoggerParameter,
    string? ExceptionParameter,
    string? LevelParameter)
{
    internal GeneratorParameterSemantics Parameter(string name) => Parameters.Single(parameter => string.Equals(parameter.Name, name, StringComparison.Ordinal));

    internal bool IsStructuredState(string parameterName, bool dynamicLevel, IReadOnlyList<Placeholder> placeholders) =>
        LoggerMessageGeneratorSemantics.IsStructuredState(Parameter(parameterName).Roles, parameterName, LevelParameter, dynamicLevel, placeholders);
}

internal static class LoggerMessageGeneratorSemantics
{
    internal const string GeneratorAssemblyName = "Microsoft.Extensions.Logging.Generators";
    internal const string SupportedAbstractionsPackageVersion = "10.0.1";
    internal const string CurrentStableAbstractionsPackageVersion = "10.0.12";
    internal const string SupportedGeneratorAssemblyVersion = "10.0.13.7005";
    internal const string CurrentStableGeneratorAssemblyVersion = "10.0.14.42308";
    internal const string GeneratorVersionIssueCode = "KMLOGP009";
    internal const string MixedGeneratorVersionIssueCode = "KMLOGP010";
    internal const string GeneratorDiagnosticIssueCode = "KMLOGP011";

    private const string LoggerMetadataName = "Microsoft.Extensions.Logging.ILogger";
    private const string LogLevelMetadataName = "Microsoft.Extensions.Logging.LogLevel";
    private const string ExceptionMetadataName = "System.Exception";
    internal static bool TryClassify(IMethodSymbol method, Compilation compilation, out LoggerMessageMethodSemantics semantics, out string? reason)
    {
        semantics = null!;
        reason = null;

        var loggerSymbol = compilation.GetTypeByMetadataName(LoggerMetadataName);
        var logLevelSymbol = compilation.GetTypeByMetadataName(LogLevelMetadataName);
        var exceptionSymbol = compilation.GetTypeByMetadataName(ExceptionMetadataName);
        if (loggerSymbol is null || logLevelSymbol is null || exceptionSymbol is null)
        {
            reason = "the compilation does not contain the pinned LoggerMessage special-parameter symbols";
            return false;
        }

        var foundLogger = false;
        var foundException = false;
        var foundLogLevel = false;
        var parameters = new List<GeneratorParameterSemantics>(method.Parameters.Length);
        foreach (var parameter in method.Parameters)
        {
            var roles = GeneratorParameterRoles.None;
            if (!foundLogger && IsBaseOrIdentity(parameter.Type, loggerSymbol, compilation))
            {
                roles |= GeneratorParameterRoles.Logger;
            }

            if (!foundException && IsBaseOrIdentity(parameter.Type, exceptionSymbol, compilation))
            {
                roles |= GeneratorParameterRoles.Exception;
            }

            if (!foundLogLevel && IsBaseOrIdentity(parameter.Type, logLevelSymbol, compilation))
            {
                roles |= GeneratorParameterRoles.LogLevel;
            }

            foundLogger |= roles.HasFlag(GeneratorParameterRoles.Logger);
            foundException |= roles.HasFlag(GeneratorParameterRoles.Exception);
            foundLogLevel |= roles.HasFlag(GeneratorParameterRoles.LogLevel);
            parameters.Add(new GeneratorParameterSemantics(parameter.Name, roles));
        }

        var loggerParameter = parameters.FirstOrDefault(parameter => parameter.Roles.HasFlag(GeneratorParameterRoles.Logger))?.Name;
        if (loggerParameter is null)
        {
            reason = "method has no generator-effective ILogger parameter";
            return false;
        }

        semantics = new LoggerMessageMethodSemantics(
            parameters,
            loggerParameter,
            parameters.FirstOrDefault(parameter => parameter.Roles.HasFlag(GeneratorParameterRoles.Exception))?.Name,
            parameters.FirstOrDefault(parameter => parameter.Roles.HasFlag(GeneratorParameterRoles.LogLevel))?.Name);
        return true;
    }

    internal static bool TryCreatePersistedModel(
        IReadOnlyList<ParameterContract> parameters,
        string loggerParameter,
        string? exceptionParameter,
        string levelSource,
        string level,
        string? levelParameter,
        out LoggerMessageMethodSemantics semantics,
        out string? reason)
    {
        semantics = null!;
        reason = null;
        var parsedParameters = new List<GeneratorParameterSemantics>(parameters.Count);
        foreach (var parameter in parameters)
        {
            if (!TryParseRole(parameter.Role, out var roles))
            {
                reason = "a persisted parameter has an unknown generator-effective role";
                return false;
            }

            parsedParameters.Add(new GeneratorParameterSemantics(parameter.Name, roles));
        }

        var firstLogger = parsedParameters.FirstOrDefault(parameter => parameter.Roles.HasFlag(GeneratorParameterRoles.Logger))?.Name;
        var firstException = parsedParameters.FirstOrDefault(parameter => parameter.Roles.HasFlag(GeneratorParameterRoles.Exception))?.Name;
        var firstLevel = parsedParameters.FirstOrDefault(parameter => parameter.Roles.HasFlag(GeneratorParameterRoles.LogLevel))?.Name;
        if (parsedParameters.Count(parameter => parameter.Roles.HasFlag(GeneratorParameterRoles.Logger)) != 1 ||
            parsedParameters.Count(parameter => parameter.Roles.HasFlag(GeneratorParameterRoles.Exception)) > 1 ||
            parsedParameters.Count(parameter => parameter.Roles.HasFlag(GeneratorParameterRoles.LogLevel)) > 1 ||
            !string.Equals(loggerParameter, firstLogger, StringComparison.Ordinal) ||
            !string.Equals(exceptionParameter, firstException, StringComparison.Ordinal))
        {
            reason = "a persisted event has inconsistent generator-effective special-parameter roles";
            return false;
        }

        var dynamicLevel = string.Equals(levelSource, "Dynamic", StringComparison.Ordinal);
        if (dynamicLevel)
        {
            if (!string.Equals(level, "Dynamic", StringComparison.Ordinal) || firstLevel is null || !string.Equals(levelParameter, firstLevel, StringComparison.Ordinal))
            {
                reason = "a persisted dynamic level source is inconsistent with its first LogLevel parameter";
                return false;
            }
        }
        else if (!string.Equals(levelParameter, firstLevel, StringComparison.Ordinal))
        {
            reason = "a persisted fixed level source is inconsistent with its first LogLevel parameter";
            return false;
        }

        semantics = new LoggerMessageMethodSemantics(parsedParameters, firstLogger!, firstException, firstLevel);
        return true;
    }

    internal static bool TryValidateTemplate(
        LoggerMessageMethodSemantics semantics,
        bool dynamicLevel,
        IReadOnlyList<Placeholder> placeholders,
        out string? reason)
    {
        foreach (var placeholder in placeholders)
        {
            var parameter = semantics.Parameters.FirstOrDefault(candidate => PlaceholderMatches(candidate.Name, placeholder.Name));
            if (parameter is null)
            {
                reason = "message placeholder does not match a method parameter";
                return false;
            }

            if (parameter.Roles.HasFlag(GeneratorParameterRoles.Logger))
            {
                reason = "message placeholder references the generator-special Logger parameter, which is outside the supported declaration scope";
                return false;
            }

            if (dynamicLevel && parameter.Roles.HasFlag(GeneratorParameterRoles.LogLevel) && string.Equals(parameter.Name, semantics.LevelParameter, StringComparison.Ordinal))
            {
                reason = "message placeholder references the generator-special DynamicLevel parameter, which is outside the supported declaration scope";
                return false;
            }
        }

        reason = null;
        return true;
    }

    internal static bool IsSupportedGeneratorVersion(IReadOnlySet<string> versions) =>
        versions.Count == 1 && (versions.Contains(SupportedGeneratorAssemblyVersion) || versions.Contains(CurrentStableGeneratorAssemblyVersion));

    internal static string GeneratorVersionFailureMessage(string? abstractionsVersion, string? generatorVersion) =>
        $"The project resolved Microsoft.Extensions.Logging.Abstractions version '{abstractionsVersion ?? "unknown"}' with Microsoft.Extensions.Logging.Generators assembly version '{generatorVersion ?? "unknown"}'; verified support is limited to the exact pairs {SupportedAbstractionsPackageVersion} / {SupportedGeneratorAssemblyVersion} and {CurrentStableAbstractionsPackageVersion} / {CurrentStableGeneratorAssemblyVersion}.";

    internal static bool IsSupportedVersionPair(string? abstractionsVersion, string? generatorVersion) =>
        string.Equals(abstractionsVersion, SupportedAbstractionsPackageVersion, StringComparison.Ordinal) &&
        string.Equals(generatorVersion, SupportedGeneratorAssemblyVersion, StringComparison.Ordinal) ||
        string.Equals(abstractionsVersion, CurrentStableAbstractionsPackageVersion, StringComparison.Ordinal) &&
        string.Equals(generatorVersion, CurrentStableGeneratorAssemblyVersion, StringComparison.Ordinal);

    internal static bool IsPinnedGeneratedCodeAttribute(AttributeData attribute, INamedTypeSymbol generatedCodeAttribute, string generatorVersion)
    {
        if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, generatedCodeAttribute) || attribute.ConstructorArguments.Length < 2)
        {
            return false;
        }

        return attribute.ConstructorArguments[0].Value is string toolName &&
            attribute.ConstructorArguments[1].Value is string version &&
            string.Equals(toolName, GeneratorAssemblyName, StringComparison.Ordinal) &&
            string.Equals(version, generatorVersion, StringComparison.Ordinal);
    }

    internal static string FormatRole(GeneratorParameterRoles roles)
    {
        if (roles == GeneratorParameterRoles.None)
        {
            return "State";
        }

        var names = new List<string>(3);
        if (roles.HasFlag(GeneratorParameterRoles.Logger)) names.Add("Logger");
        if (roles.HasFlag(GeneratorParameterRoles.Exception)) names.Add("Exception");
        if (roles.HasFlag(GeneratorParameterRoles.LogLevel)) names.Add("LogLevel");
        return string.Join('|', names);
    }

    internal static bool TryParseRole(string? role, out GeneratorParameterRoles roles)
    {
        roles = GeneratorParameterRoles.None;
        if (string.Equals(role, "State", StringComparison.Ordinal))
        {
            return true;
        }

        // DynamicLevel was the pre-remediation spelling. Read it as the same
        // semantic role so existing dynamic baselines remain readable.
        if (string.Equals(role, "DynamicLevel", StringComparison.Ordinal))
        {
            roles = GeneratorParameterRoles.LogLevel;
            return true;
        }

        if (string.IsNullOrWhiteSpace(role))
        {
            return false;
        }

        foreach (var name in role.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var parsed = name switch
            {
                "Logger" => GeneratorParameterRoles.Logger,
                "Exception" => GeneratorParameterRoles.Exception,
                "LogLevel" => GeneratorParameterRoles.LogLevel,
                _ => GeneratorParameterRoles.None
            };
            if (parsed == GeneratorParameterRoles.None || roles.HasFlag(parsed))
            {
                roles = GeneratorParameterRoles.None;
                return false;
            }

            roles |= parsed;
        }

        return roles != GeneratorParameterRoles.None && string.Equals(FormatRole(roles), role, StringComparison.Ordinal);
    }

    internal static bool IsStructuredState(
        GeneratorParameterRoles roles,
        string parameterName,
        string? levelParameter,
        bool dynamicLevel,
        IReadOnlyList<Placeholder> placeholders)
    {
        if (roles == GeneratorParameterRoles.None || roles.HasFlag(GeneratorParameterRoles.LogLevel) && !dynamicLevel)
        {
            return true;
        }

        if (roles.HasFlag(GeneratorParameterRoles.Logger) ||
            roles.HasFlag(GeneratorParameterRoles.LogLevel) && string.Equals(parameterName, levelParameter, StringComparison.Ordinal))
        {
            return false;
        }

        return roles.HasFlag(GeneratorParameterRoles.Exception) && placeholders.Any(placeholder => PlaceholderMatches(parameterName, placeholder.Name));
    }

    internal static bool PlaceholderMatches(string parameterName, string placeholderName) =>
        string.Equals(parameterName, placeholderName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals("@" + parameterName, placeholderName, StringComparison.OrdinalIgnoreCase);

    internal static string EmittedName(string parameterName, IReadOnlyList<Placeholder> placeholders) =>
        placeholders.FirstOrDefault(placeholder => PlaceholderMatches(parameterName, placeholder.Name))?.Name.TrimStart('@') ?? parameterName;

    private static bool IsBaseOrIdentity(ITypeSymbol source, ITypeSymbol destination, Compilation compilation)
    {
        var conversion = ((CSharpCompilation)compilation).ClassifyConversion(source, destination);
        return conversion.IsIdentity || (conversion.IsReference && conversion.IsImplicit);
    }
}
