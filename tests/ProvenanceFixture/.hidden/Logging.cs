using Microsoft.Extensions.Logging;

namespace ProvenanceFixture.Hidden;

public static partial class HiddenLogging
{
    [LoggerMessage(EventId = 2002, Level = LogLevel.Information, Message = "Dot-prefixed {Value}")]
    public static partial void DotPrefixed(ILogger logger, string value);
}
