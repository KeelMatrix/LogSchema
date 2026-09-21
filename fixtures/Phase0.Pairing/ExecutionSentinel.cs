using System.Runtime.CompilerServices;

namespace Phase0.Pairing;

internal static class ExecutionSentinel
{
    [ModuleInitializer]
    internal static void ModuleInitializer() => ThrowIfExecuted();

    internal static void ThrowIfExecuted() => throw new InvalidOperationException("Target application code executed.");
}
