using Microsoft.Extensions.Logging;

namespace Phase0.Mixed.CurrentStable;

public static partial class Logging
{
    [LoggerMessage(EventId = 1821, Level = LogLevel.Information, Message = "Current stable line {Value}")]
    public static partial void CurrentStable(ILogger logger, string value);
}
