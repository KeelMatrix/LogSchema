using Microsoft.Extensions.Logging;

namespace GeneratorOracleStableFixture;

public static partial class Logging
{
    [LoggerMessage(EventId = 101, Level = LogLevel.Information, Message = "Fast {value}")]
    public static partial void Fast(ILogger logger, int value);

    [LoggerMessage(EventId = 102, Level = LogLevel.Information, Message = "Escaped {@value}")]
    public static partial void EscapedPlaceholder(ILogger logger, int value);

    [LoggerMessage(EventId = 103, Level = LogLevel.Information, Message = "Absent")]
    public static partial void EscapedParameter(ILogger logger, int @value);

    [LoggerMessage(EventId = 104, Level = LogLevel.Information, Message = "Repeated {Value} {Value}")]
    public static partial void Repeated(ILogger logger, int value);

    [LoggerMessage(EventId = 105, Level = LogLevel.Information, Message = "Formatted {value,8:0000}")]
    public static partial void AlignmentAndFormat(ILogger logger, int value);

    [LoggerMessage(EventId = 106, Level = LogLevel.Information, Message = "Unicode {ΔELTA}")]
    public static partial void Unicode(ILogger logger, int δelta);

    [LoggerMessage(EventId = 107, Message = "Dynamic {value}")]
    public static partial void Dynamic(ILogger logger, LogLevel level, int value);

    [LoggerMessage(EventId = 108, Level = LogLevel.Information, Message = "Six {one} {two} {three} {four} {five} {six}")]
    public static partial void Six(ILogger logger, int one, int two, int three, int four, int five, int six);

    [LoggerMessage(EventId = 109, Level = LogLevel.Information, Message = "Seven {one} {two} {three} {four} {five} {six} {seven}")]
    public static partial void Seven(ILogger logger, int one, int two, int three, int four, int five, int six, int seven);
}
