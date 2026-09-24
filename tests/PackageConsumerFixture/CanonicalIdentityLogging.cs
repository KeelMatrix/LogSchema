using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

[SuppressMessage("Design", "CA1050:Declare types in namespaces", Justification = "The manifest regression fixture requires the canonical global type identity.")]
public static partial class Logging
{
    [LoggerMessage(EventId = 1100, Level = LogLevel.Information, Message = "Event {First} {Second} {Third}")]
    public static partial void Event(ILogger logger, string first, string second, string third);

    [LoggerMessage(EventId = 1101, Level = LogLevel.Error, Message = "Derived exception")]
    public static partial void DerivedException(ILogger logger, DerivedProblem exception);

    [LoggerMessage(EventId = 1102, Level = LogLevel.Error, Message = "Exact exception")]
    public static partial void ExactException(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1103, Level = LogLevel.Information, Message = "Ordinary custom {Value}")]
    public static partial void OrdinaryCustom(ILogger logger, OrdinaryProblem value);
}

[SuppressMessage("Design", "CA1050:Declare types in namespaces", Justification = "The global type keeps the canonical identity fixture stable.")]
[SuppressMessage("Naming", "CA1710:Identifiers should have correct suffix", Justification = "A non-Exception suffix proves derived-exception support independently of type-name suffixes.")]
public sealed class DerivedProblem : Exception
{
}

[SuppressMessage("Design", "CA1050:Declare types in namespaces", Justification = "The global type keeps the canonical identity fixture stable.")]
public sealed class OrdinaryProblem
{
}
