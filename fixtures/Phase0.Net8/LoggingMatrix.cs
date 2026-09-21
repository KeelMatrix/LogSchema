using Microsoft.Extensions.Logging;

namespace Phase0.Net8;

internal static class MatrixConstants
{
    internal const int ConstantEventId = 1001;
    internal const string ConstantEventName = "OrderCreated";
    internal const LogLevel ConstantLevel = LogLevel.Warning;
}

public static partial class LoggingMatrix
{
    [LoggerMessage(EventId = MatrixConstants.ConstantEventId, EventName = MatrixConstants.ConstantEventName, Level = MatrixConstants.ConstantLevel, Message = "Order {OrderId} for {CustomerName}")]
    public static partial void ConstantArguments(ILogger logger, int orderId, string customerName);

    [LoggerMessage(MatrixConstants.ConstantEventId + 1, LogLevel.Error, "Payment failed for {OrderId}")]
    public static partial void ConstructorArguments(ILogger logger, Exception exception, int orderId);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Information, Message = "The default event name is the method name for {OrderId}")]
    public static partial void DefaultEventName(ILogger logger, int orderId);

    [LoggerMessage(EventId = 1004, EventName = "ExplicitEventName", Level = LogLevel.Debug, Message = "Explicit name {RequestId}")]
    public static partial void ExplicitEventName(this ILogger logger, Guid requestId);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Information, Message = "Logger and level parameters {RequestId}")]
    public static partial void LoggerAndLevel(ILogger logger, LogLevel level, Guid requestId);

    [LoggerMessage(EventId = 1006, Level = LogLevel.Critical, Message = "Unicode событие {Идентификатор}")]
    public static partial void UnicodeTemplate(ILogger logger, string Идентификатор);

    [LoggerMessage(EventId = 1007, Level = LogLevel.Trace, Message = "Escaped {{literal}} and {Value:000}")]
    public static partial void FormatSpecifier(ILogger logger, int Value);

    [LoggerMessage(EventId = 1008, EventName = "Constant" + "Expression", Level = LogLevel.Information, Message = "Constant expression {Value}")]
    public static partial void ConstantExpressionEventName(ILogger logger, int value);

    [LoggerMessage(EventId = 1009, Level = LogLevel.Information, Message = "Skip enabled check {Value}", SkipEnabledCheck = true)]
    public static partial void SkipEnabledCheck(ILogger logger, int value);

    [LoggerMessage(eventId: 1010, level: LogLevel.Information, message: "All named arguments {Value}", EventName = "AllNamedArguments")]
    public static partial void AllNamedArguments(ILogger logger, int value);

    [LoggerMessage(EventId = 1011, Level = LogLevel.Information, Message = "Duplicate event one {Value}")]
    public static partial void DuplicateEventIdOne(ILogger logger, int value);

    [LoggerMessage(EventId = 1011, Level = LogLevel.Warning, Message = "Duplicate event two {Value}")]
    public static partial void DuplicateEventIdTwo(ILogger logger, int value);

    [LoggerMessage(EventId = 1012, Level = LogLevel.Information, Message = "Generic event {Value}")]
    public static partial void GenericEvent<T>(ILogger logger, T value);

    [LoggerMessage(EventId = 1013, Level = LogLevel.Information, Message = "No parameters")]
    public static partial void NoParameters();

    [LoggerMessage(EventId = 1099, Level = LogLevel.Warning, Message = "This declaration is not a supported generated method")]
    public static void NonPartialWithExplicitValues(ILogger logger)
    {
    }

    static LoggingMatrix()
    {
        throw new InvalidOperationException("The fixture must never execute.");
    }
}

public partial class PartialOuter
{
    public static partial class NestedLogging
    {
        [LoggerMessage(EventId = 1014, Level = LogLevel.Information, Message = "Nested event {Value}")]
        public static partial void NestedEvent(ILogger logger, string value);
    }
}

public partial class InstanceLoggingMatrix
{
    [LoggerMessage(EventId = 1015, Level = LogLevel.Information, Message = "Instance event {Value}")]
    public partial void InstanceEvent(ILogger logger, string value);
}
