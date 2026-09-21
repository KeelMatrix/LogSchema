namespace Phase0.Multi;

internal static class ExecutionSentinel
{
    internal static void ThrowIfExecuted() => throw new InvalidOperationException("Target application code executed.");
}

