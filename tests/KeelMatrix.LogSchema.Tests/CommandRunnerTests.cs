using KeelMatrix.LogSchema;

namespace KeelMatrix.LogSchema.Tests;

public sealed class CommandRunnerTests
{
    [Fact]
    public async Task HelpDocumentsCommandsAndReservedTelemetryFlag()
    {
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var exitCode = await CommandRunner.RunAsync(["--help"], output, errors);
        Assert.Equal(0, exitCode);
        Assert.Contains("logschema capture", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("--no-telemetry", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(errors.ToString());
    }

    [Fact]
    public async Task InvalidInvocationReturnsTwoAndJsonSeparatesToolErrors()
    {
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var exitCode = await CommandRunner.RunAsync(["diff", "one.json", "--format", "json"], output, errors);
        Assert.Equal(2, exitCode);
        Assert.Contains("toolErrors", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("two manifest files", output.ToString(), StringComparison.Ordinal);
    }
}
