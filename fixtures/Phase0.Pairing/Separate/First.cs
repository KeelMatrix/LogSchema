using Microsoft.Extensions.Logging;

namespace Phase0.Pairing;

public static partial class SeparateIdentityLogging
{
    [LoggerMessage(EventId = 1201, Level = LogLevel.Information, Message = "Separate identity first {Value}")]
    public static partial void SameIdentity(ILogger logger, string value);
}
