using Microsoft.Extensions.Logging;

namespace Phase0.Multi;

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

    [LoggerMessage(EventId = 1099, Level = LogLevel.Warning, Message = "This declaration is not a supported generated method")]
    public static void NonPartialWithExplicitValues(ILogger logger)
    {
    }

    static LoggingMatrix()
    {
        throw new InvalidOperationException("The fixture must never execute.");
    }
}
