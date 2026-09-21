namespace Phase0.Stable;

internal static class ExecutionSentinel
{
    internal static void ThrowIfExecuted() => throw new InvalidOperationException("Target application code executed.");
}

