using Microsoft.Extensions.Logging;

namespace Phase0.Mismatch10_1Generator10_14;

public static partial class Logging
{
    [LoggerMessage(EventId = 1811, Level = LogLevel.Information, Message = "Mismatched package and generator {Value}")]
    public static partial void Mismatch(ILogger logger, string value);
}
