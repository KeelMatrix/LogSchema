using System;
using System.CodeDom.Compiler;
using Microsoft.Extensions.Logging;

namespace Phase0.FakeGenerated;

[AttributeUsage(AttributeTargets.Method)]
internal sealed class AliasGeneratedCodeAttribute : Attribute
{
}

public static partial class Logging
{
    [GeneratedCode("Microsoft.Extensions.Logging.Generators", "10.0.13.7005")]
    [AliasGeneratedCode]
    public static partial void UserAuthoredFake(ILogger logger, string value)
    {
    }
}
