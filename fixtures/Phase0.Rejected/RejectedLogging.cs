using Microsoft.Extensions.Logging;

namespace Phase0.Rejected;

public static partial class RejectedLogging
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Out {Value}")]
    public static partial void Out(ILogger logger, out int value);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Ref {Value}")]
    public static partial void Ref(ILogger logger, ref int value);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "In {Value}")]
    public static partial void In(ILogger logger, in int value);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Params {Value}")]
    public static partial void Params(ILogger logger, params int[] value);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Ref readonly {Value}")]
    public static partial void RefReadonly(ILogger logger, ref readonly int value);
}
