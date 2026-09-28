using Microsoft.Extensions.Logging;

namespace Phase0.Mismatch10_12Generator10_13;

public static partial class Logging
{
    [LoggerMessage(EventId = 1810, Level = LogLevel.Information, Message = "Mismatched package and generator {Value}")]
    public static partial void Mismatch(ILogger logger, string value);
}
