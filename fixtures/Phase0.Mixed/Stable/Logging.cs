using Microsoft.Extensions.Logging;

namespace Phase0.Mixed.Stable;

public static partial class Logging
{
    [LoggerMessage(EventId = 1820, Level = LogLevel.Information, Message = "Stable line {Value}")]
    public static partial void Stable(ILogger logger, string value);
}
