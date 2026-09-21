using Microsoft.Extensions.Logging;

namespace Phase0.Pairing;

public static partial class IdentityDimensionsLogging
{
    [LoggerMessage(EventId = 1205, Level = LogLevel.Information, Message = "Value identity {Value}")]
    public static partial void RefKindIdentity(ILogger logger, int value);

    [LoggerMessage(EventId = 1206, Level = LogLevel.Warning, Message = "Ref identity {Value}")]
    public static partial void RefKindIdentity(ILogger logger, ref int value);

    [LoggerMessage(EventId = 1207, Level = LogLevel.Information, Message = "Generic one {Value}")]
    public static partial void GenericArityIdentity<T>(ILogger logger, object value);

    [LoggerMessage(EventId = 1208, Level = LogLevel.Warning, Message = "Generic two {Value}")]
    public static partial void GenericArityIdentity<T1, T2>(ILogger logger, object value);
}
