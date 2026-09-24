using Microsoft.Extensions.Logging;

namespace ProvenanceFixture.InProject;

public static partial class SharedLogging
{
    [LoggerMessage(EventId = 2001, Level = LogLevel.Information, Message = "In-project {Value}")]
    public static partial void InProject(ILogger logger, string value);
}
