using Microsoft.Extensions.Logging;

namespace Phase0.UnsupportedGenerator;

public static partial class Logging
{
    [LoggerMessage(EventId = 1800, Level = LogLevel.Information, Message = "Unsupported generator version {Value}")]
    public static partial void Unsupported(ILogger logger, string value);
}
