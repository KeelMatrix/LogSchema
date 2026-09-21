using Microsoft.Extensions.Logging;

namespace Phase0.Pairing;

public static partial class SameDocumentIdentityLogging
{
    [LoggerMessage(EventId = 1203, Level = LogLevel.Information, Message = "Same document first {Value}")]
    public static partial void SameIdentity(ILogger logger, string value);

    [LoggerMessage(EventId = 1204, Level = LogLevel.Warning, Message = "Same document second {Value}")]
    public static partial void SameIdentity(ILogger logger, string value);
}
