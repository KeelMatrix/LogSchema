using Microsoft.Extensions.Logging;

namespace Phase0.Pairing;

public static partial class SeparateIdentityLogging
{
    [LoggerMessage(EventId = 1202, Level = LogLevel.Warning, Message = "Separate identity second {Value}")]
    public static partial void SameIdentity(ILogger logger, string value);
}
