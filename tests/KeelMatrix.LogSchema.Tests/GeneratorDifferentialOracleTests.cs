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
        await using var tool = await InstalledToolHarness.CreateAsync();
        await AssertFixtureMatchesGenerator(
            tool,
            FindRepositoryFile("tests", "GeneratorOracleFixture", "GeneratorOracleFixture.csproj"),
            InvokePrimary);
        await AssertFixtureMatchesGenerator(
            tool,
            FindRepositoryFile("tests", "GeneratorOracleStableFixture", "GeneratorOracleStableFixture.csproj"),
            InvokeStable);
    }

    [Fact]
    public async Task RepeatedStructuredPrefixMutationsAreBreakingInBothDirectionsForBothSupportedPairs()
    {
        await using var tool = await InstalledToolHarness.CreateAsync();
        foreach (var fixtureName in new[] { "GeneratorOracleFixture", "GeneratorOracleStableFixture" })
        {
            var root = Directory.CreateTempSubdirectory("logschema-generator-oracle-");
            var sourceProject = FindRepositoryFile("tests", fixtureName);
            var copiedProject = Path.Combine(root.FullName, fixtureName);
            CopyDirectory(sourceProject, copiedProject);
            var projectPath = Path.Combine(copiedProject, fixtureName + ".csproj");
            var sourcePath = Path.Combine(copiedProject, "Logging.cs");
            var baselinePath = Path.Combine(root.FullName, "baseline.json");
            var mutatedPath = Path.Combine(root.FullName, "mutated.json");
            var reversePath = Path.Combine(root.FullName, "reverse.json");
            try
            {
                await RestoreAsync(projectPath);
                await CaptureAsync(tool, projectPath, baselinePath);
                await AssertClean(tool, projectPath, baselinePath);
                Assert.Equal("@value", await ReadEmittedNameAsync(baselinePath, "EscapedPlaceholder"));

                var original = await File.ReadAllTextAsync(sourcePath);
                var repeated = original.Replace("Escaped {@value}", "Escaped {@value} {@value}", StringComparison.Ordinal);
                Assert.NotEqual(original, repeated);
                await File.WriteAllTextAsync(sourcePath, repeated);
                await CaptureAsync(tool, projectPath, mutatedPath);
                Assert.Equal("value", await ReadEmittedNameAsync(mutatedPath, "EscapedPlaceholder"));
                await AssertBreakingStateChange(tool, projectPath, baselinePath, mutatedPath, "EscapedPlaceholder");

                await File.WriteAllTextAsync(sourcePath, original);
                await CaptureAsync(tool, projectPath, reversePath);
                await AssertBreakingStateChange(tool, projectPath, mutatedPath, reversePath, "EscapedPlaceholder");
                await AssertClean(tool, projectPath, baselinePath);
            }
            finally
            {
                root.Delete(true);
            }
        }
    }

    private static async Task AssertFixtureMatchesGenerator(InstalledToolHarness tool, string projectPath, Action<ILogger> invoke)
    {
        var root = Directory.CreateTempSubdirectory("logschema-generator-oracle-manifest-");
        var manifestPath = Path.Combine(root.FullName, "manifest.json");
        try
        {
            await CaptureAsync(tool, projectPath, manifestPath);
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
            Assert.Equal(["first", "second", "{OriginalFormat}"], actual["Mixed"]);
        }
        finally
        {
            root.Delete(true);
        }
    }

    private static async Task AssertBreakingStateChange(InstalledToolHarness tool, string projectPath, string baselinePath, string currentPath, string method)
    {
        var check = await tool.RunAsync("check", projectPath, "--baseline", baselinePath, "--format", "json", "--severity", "all", "--accept", "KMLOG301", "--no-telemetry");
        AssertBreakingEnvelope(check, method);

        var diff = await tool.RunAsync("diff", baselinePath, currentPath, "--format", "json", "--severity", "all", "--accept", "KMLOG301", "--no-telemetry");
        AssertBreakingEnvelope(diff, method);
    }

    private static void AssertBreakingEnvelope(InstalledToolResult result, string method)
    {
        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardError);
        using var envelope = JsonDocument.Parse(result.StandardOutput);
        var finding = envelope.RootElement.GetProperty("findings").EnumerateArray().Single(item => item.GetProperty("eventName").GetString() == method);
        Assert.Equal("KMLOG102", finding.GetProperty("code").GetString());
        Assert.False(envelope.RootElement.GetProperty("analysisErrors").EnumerateArray().Any());
        Assert.True(envelope.RootElement.GetProperty("coverageComplete").GetBoolean());
    }

    private static async Task AssertClean(InstalledToolHarness tool, string projectPath, string baselinePath)
    {
        var check = await tool.RunAsync("check", projectPath, "--baseline", baselinePath, "--format", "json", "--severity", "all", "--accept", "KMLOG301", "--no-telemetry");
        Assert.Equal(0, check.ExitCode);
        Assert.Empty(check.StandardError);
        var diff = await tool.RunAsync("diff", baselinePath, baselinePath, "--format", "json", "--severity", "all", "--accept", "KMLOG301", "--no-telemetry");
        Assert.Equal(0, diff.ExitCode);
        Assert.Empty(diff.StandardError);
    }

    private static async Task CaptureAsync(InstalledToolHarness tool, string projectPath, string manifestPath)
    {
        var result = await tool.RunAsync("capture", projectPath, "--output", manifestPath, "--no-telemetry");
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
    }

    private static async Task<string> ReadEmittedNameAsync(string manifestPath, string method)
    {
        var manifest = await ManifestJson.ReadAsync(manifestPath, CancellationToken.None);
        return Assert.Single(manifest.Events, @event => string.Equals(@event.Method, method, StringComparison.Ordinal)).StructuredState.Single().EmittedName;
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

    private sealed record InstalledToolResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class InstalledToolHarness : IAsyncDisposable
    {
        private readonly string root;
        private readonly string commandPath;

        private InstalledToolHarness(string root, string commandPath)
        {
            this.root = root;
            this.commandPath = commandPath;
        }

        internal static async Task<InstalledToolHarness> CreateAsync()
        {
            var root = Directory.CreateTempSubdirectory("logschema-installed-tool-");
            var feed = Path.Combine(root.FullName, "feed");
            var toolPath = Path.Combine(root.FullName, "tool");
            Directory.CreateDirectory(feed);
            Directory.CreateDirectory(toolPath);

            var projectPath = FindRepositoryFile("src", "KeelMatrix.LogSchema", "KeelMatrix.LogSchema.csproj");
            var pack = await RunProcessAsync("dotnet", ["pack", projectPath, "-c", "Release", "--no-restore", "--nologo", "-o", feed]);
            Assert.Equal(0, pack.ExitCode);
            Assert.Empty(pack.StandardError);

            var packagePath = Assert.Single(Directory.GetFiles(feed, "KeelMatrix.LogSchema.*.nupkg"));
            var packageVersion = Path.GetFileNameWithoutExtension(packagePath)["KeelMatrix.LogSchema.".Length..];
            var install = await RunProcessAsync("dotnet", ["tool", "install", "KeelMatrix.LogSchema", "--tool-path", toolPath, "--version", packageVersion, "--add-source", feed, "--configfile", FindRepositoryFile("NuGet.config"), "--no-cache", "--ignore-failed-sources"]);
            Assert.Equal(0, install.ExitCode);
            Assert.Empty(install.StandardError);

            var commandName = OperatingSystem.IsWindows() ? "logschema.exe" : "logschema";
            var commandPath = Path.Combine(toolPath, commandName);
            Assert.True(File.Exists(commandPath), $"installed tool command was not found at {commandPath}");
            return new InstalledToolHarness(root.FullName, commandPath);
        }

        internal Task<InstalledToolResult> RunAsync(params string[] arguments) => RunProcessAsync(commandPath, arguments);

        public ValueTask DisposeAsync()
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return ValueTask.CompletedTask;
        }

        private static async Task<InstalledToolResult> RunProcessAsync(string fileName, IReadOnlyList<string> arguments)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            Assert.True(process.Start(), $"could not start {fileName}");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return new InstalledToolResult(process.ExitCode, await outputTask, await errorTask);
        }
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
        PrimaryLogging.Mixed(logger, 1, 2);
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
        StableLogging.Mixed(logger, 1, 2);
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
