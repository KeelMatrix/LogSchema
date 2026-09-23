using Microsoft.Extensions.Logging;

namespace PackageConsumerFixture;

public static partial class ConsumerLogging
{
    [LoggerMessage(EventId = 1001, EventName = "ConsumerProcessed", Level = LogLevel.Information, Message = "Processed {OrderId}")]
    public static partial void Processed(ILogger logger, int orderId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Omitted event id {Value}")]
    public static partial void OmittedEventId(ILogger logger, int value);

    [LoggerMessage(Message = "Dynamic level {Value}")]
    public static partial void DynamicLevel(ILogger logger, LogLevel level, int value);

    [LoggerMessage(Level = LogLevel.None, Message = "Fixed none {Value}")]
    public static partial void FixedNone(ILogger logger, int value);
}
