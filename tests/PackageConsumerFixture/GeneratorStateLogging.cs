using Microsoft.Extensions.Logging;

namespace PackageConsumerFixture;

public static partial class GeneratorStateLogging
{
    [LoggerMessage(EventId = 1200, Level = LogLevel.Information, Message = "Processing")]
    public static partial void AbsentState(ILogger logger, int customerId);

    [LoggerMessage(EventId = 1201, Level = LogLevel.Information, Message = "Order {Second} {First}")]
    public static partial void MethodOrder(ILogger logger, int first, int second);

    [LoggerMessage(EventId = 1202, Level = LogLevel.Information, Message = "Repeat {Value} {Value}")]
    public static partial void RepeatedPlaceholder(ILogger logger, int value);

    [LoggerMessage(EventId = 1203, Level = LogLevel.Information, Message = "Case {DisplayValue}")]
    public static partial void PlaceholderCasing(ILogger logger, int displayValue);

    [LoggerMessage(EventId = 1204, Level = LogLevel.Information, Message = "Removed")]
    public static partial void PlaceholderRemoved(ILogger logger, int value);

    [LoggerMessage(EventId = 1205, Level = LogLevel.Error, Message = "Failed {exception}")]
    public static partial void SpecialExceptionInTemplate(ILogger logger, DerivedProblem exception);

    [LoggerMessage(EventId = 1206, Level = LogLevel.Error, Message = "Failed")]
    public static partial void MultipleExceptions(ILogger logger, DerivedProblem first, DerivedProblem second);

    [LoggerMessage(EventId = 1207, Level = LogLevel.Information, Message = "Later logger {laterLogger}")]
    public static partial void MultipleLoggers(ILogger firstLogger, ILogger laterLogger);

    [LoggerMessage(EventId = 1208, Message = "Later level {laterLevel}")]
    public static partial void MultipleDynamicLevels(ILogger logger, LogLevel firstLevel, LogLevel laterLevel);

    [LoggerMessage(EventId = 1209, Level = LogLevel.Information, Message = "Fixed level {level}")]
    public static partial void FixedLevelParameter(ILogger logger, LogLevel level);

    [LoggerMessage(EventId = 1210, Level = LogLevel.Error, Message = "Role flip")]
    public static partial void RoleFlip(ILogger logger, RoleFlipProblem problem);
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1710:Identifiers should have correct suffix", Justification = "The role-transition fixture intentionally uses a stable non-Exception type name.")]
public sealed class RoleFlipProblem : Exception
{
}
