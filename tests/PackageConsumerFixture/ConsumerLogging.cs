using Microsoft.Extensions.Logging;

namespace PackageConsumerFixture;

public static partial class ConsumerLogging
{
    [LoggerMessage(EventId = 1001, EventName = "ConsumerProcessed", Level = LogLevel.Information, Message = "Processed {OrderId}")]
    public static partial void Processed(ILogger logger, int orderId);
}
