using Microsoft.Extensions.Logging;

namespace ProvenanceFixture.Linked;

public static partial class LinkedLogging
{
    [LoggerMessage(EventId = 2003, Level = LogLevel.Information, Message = "Linked {Value}")]
    public static partial void Linked(ILogger logger, string value);
}
