using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using KeelMatrix.LogSchema;
using GeneratorOracleFixture;
using StableLogging = GeneratorOracleStableFixture.Logging;
using PrimaryLogging = GeneratorOracleFixture.Logging;

namespace KeelMatrix.LogSchema.Tests;

public sealed class GeneratorDifferentialOracleTests
{
    [Fact]
    public async Task InstalledToolMatchesRealGeneratorStateForBothSupportedPairs()
    {
        await AssertFixtureMatchesGenerator(
            FindRepositoryFile("tests", "GeneratorOracleFixture", "GeneratorOracleFixture.csproj"),
            InvokePrimary);
        await AssertFixtureMatchesGenerator(
            FindRepositoryFile("tests", "GeneratorOracleStableFixture", "GeneratorOracleStableFixture.csproj"),
            InvokeStable);
    }

    [Fact]
    public async Task StateKeyMutationsAreBreakingEvenWhenTemplateTextAlsoChanges()
    {
        var root = Directory.CreateTempSubdirectory("logschema-generator-oracle-");
        var sourceProject = FindRepositoryFile("tests", "GeneratorOracleFixture");
        var copiedProject = Path.Combine(root.FullName, "GeneratorOracleFixture");
        CopyDirectory(sourceProject, copiedProject);
        var projectPath = Path.Combine(copiedProject, "GeneratorOracleFixture.csproj");
        var sourcePath = Path.Combine(copiedProject, "Logging.cs");
        var baselinePath = Path.Combine(root.FullName, "baseline.json");
        try
        {
            await RestoreAsync(projectPath);
            await CaptureAsync(projectPath, baselinePath);
            var original = await File.ReadAllTextAsync(sourcePath);

            await File.WriteAllTextAsync(sourcePath, original.Replace("Escaped {@value}", "Escaped {value}", StringComparison.Ordinal));
            await AssertBreakingStateChange(projectPath, baselinePath, "EscapedPlaceholder");

            await File.WriteAllTextAsync(sourcePath, original.Replace("int @value", "int value", StringComparison.Ordinal));
            await AssertBreakingStateChange(projectPath, baselinePath, "EscapedParameter");
        }
        finally
        {
            root.Delete(true);
        }
    }

    private static async Task AssertFixtureMatchesGenerator(string projectPath, Action<ILogger> invoke)
    {
        var root = Directory.CreateTempSubdirectory("logschema-generator-oracle-manifest-");
        var manifestPath = Path.Combine(root.FullName, "manifest.json");
        try
        {
            await CaptureAsync(projectPath, manifestPath);
            var manifest = await ManifestJson.ReadAsync(manifestPath, CancellationToken.None);
            var logger = new CapturingLogger();
            invoke(logger);

            var expected = manifest.Events.ToDictionary(
                @event => @event.Method,
                @event => @event.StructuredState.Select(property => property.EmittedName).Append("{OriginalFormat}").ToArray(),
                StringComparer.Ordinal);
            var actual = logger.Events.ToDictionary(item => item.Name, item => item.Keys.ToArray(), StringComparer.Ordinal);
            Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
            foreach (var method in expected.Keys)
            {
                Assert.Equal(expected[method], actual[method]);
            }

            Assert.Contains("@value", actual["EscapedPlaceholder"]);
            Assert.Contains("@value", actual["EscapedParameter"]);
            Assert.Equal(["one", "two", "three", "four", "five", "six", "{OriginalFormat}"], actual["Six"]);
            Assert.Equal(["one", "two", "three", "four", "five", "six", "seven", "{OriginalFormat}"], actual["Seven"]);
        }
        finally
        {
            root.Delete(true);
        }
    }

    private static async Task AssertBreakingStateChange(string projectPath, string baselinePath, string method)
    {
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var exitCode = await CommandRunner.RunAsync(["check", projectPath, "--baseline", baselinePath, "--format", "json", "--severity", "all", "--no-telemetry"], output, errors);
        Assert.Equal(1, exitCode);
        Assert.Empty(errors.ToString());
        using var envelope = JsonDocument.Parse(output.ToString());
        var finding = envelope.RootElement.GetProperty("findings").EnumerateArray().Single(item => item.GetProperty("eventName").GetString() == method);
        Assert.Equal("KMLOG102", finding.GetProperty("code").GetString());
        Assert.False(envelope.RootElement.GetProperty("analysisErrors").EnumerateArray().Any());
        Assert.True(envelope.RootElement.GetProperty("coverageComplete").GetBoolean());
    }

    private static async Task CaptureAsync(string projectPath, string manifestPath)
    {
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var exitCode = await CommandRunner.RunAsync(["capture", projectPath, "--output", manifestPath, "--no-telemetry"], output, errors);
        Assert.Equal(0, exitCode);
        Assert.Empty(errors.ToString());
    }

    private static async Task RestoreAsync(string projectPath)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"restore \"{projectPath}\" --configfile \"{FindRepositoryFile("NuGet.config")}\" --nologo",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        Assert.NotNull(process);
        await process!.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync());
    }

    private static void InvokePrimary(ILogger logger)
    {
        PrimaryLogging.Fast(logger, 1);
        PrimaryLogging.EscapedPlaceholder(logger, 2);
        PrimaryLogging.EscapedParameter(logger, 3);
        PrimaryLogging.Repeated(logger, 4);
        PrimaryLogging.AlignmentAndFormat(logger, 5);
        PrimaryLogging.Unicode(logger, 6);
        PrimaryLogging.Dynamic(logger, LogLevel.Warning, 7);
        PrimaryLogging.Six(logger, 1, 2, 3, 4, 5, 6);
        PrimaryLogging.Seven(logger, 1, 2, 3, 4, 5, 6, 7);
    }

    private static void InvokeStable(ILogger logger)
    {
        StableLogging.Fast(logger, 1);
        StableLogging.EscapedPlaceholder(logger, 2);
        StableLogging.EscapedParameter(logger, 3);
        StableLogging.Repeated(logger, 4);
        StableLogging.AlignmentAndFormat(logger, 5);
        StableLogging.Unicode(logger, 6);
        StableLogging.Dynamic(logger, LogLevel.Warning, 7);
        StableLogging.Six(logger, 1, 2, 3, 4, 5, 6);
        StableLogging.Seven(logger, 1, 2, 3, 4, 5, 6, 7);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            if (Path.GetFileName(directory) is "bin" or "obj")
            {
                continue;
            }

            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private static string FindRepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LogSchema.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine([directory!.FullName, .. parts]);
    }

    private sealed class CapturingLogger : ILogger
    {
        internal List<(string Name, List<string> Keys)> Events { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var keys = state is IEnumerable enumerable
                ? enumerable.Cast<object>().Select(value => value is KeyValuePair<string, object?> pair ? pair.Key : value.ToString() ?? string.Empty).ToList()
                : [];
            Events.Add((eventId.Name ?? eventId.Id.ToString(CultureInfo.InvariantCulture), keys));
        }
    }
}
