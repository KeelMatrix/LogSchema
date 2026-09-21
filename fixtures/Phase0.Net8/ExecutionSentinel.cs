namespace Phase0.Net8;

internal static class ExecutionSentinel
{
    internal static void ThrowIfExecuted() => throw new InvalidOperationException("Target application code executed.");
}

