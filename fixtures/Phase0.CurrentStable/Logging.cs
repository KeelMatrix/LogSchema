using Microsoft.Extensions.Logging;

namespace Phase0.CurrentStable;

public static partial class Logging
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Current stable {Value}")]
    public static partial void CurrentStable(ILogger logger, string value);
}
