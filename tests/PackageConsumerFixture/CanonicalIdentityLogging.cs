using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

[SuppressMessage("Design", "CA1050:Declare types in namespaces", Justification = "The manifest regression fixture requires the canonical global type identity.")]
public static partial class Logging
{
    [LoggerMessage(EventId = 1100, Level = LogLevel.Information, Message = "Event {First} {Second} {Third}")]
    public static partial void Event(ILogger logger, string first, string second, string third);
}
