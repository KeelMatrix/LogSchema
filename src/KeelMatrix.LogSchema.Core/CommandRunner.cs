using System.Text.Json;
using System.Text.Json.Serialization;

namespace KeelMatrix.LogSchema;

internal sealed class CommandRunner
{
    private static readonly JsonSerializerOptions OutputOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken = default)
    {
        ParsedArguments parsed;
        try
        {
            parsed = Parse(args);
        }
        catch (InvocationException exception)
        {
            parsed = new ParsedArguments(null, [], exception.Message, DetectRequestedFormat(args), SeverityGate.Breaking, new HashSet<string>(StringComparer.Ordinal), null, null, null);
        }
        if (parsed.ShowHelp)
        {
            await stdout.WriteAsync(CliHelp.Text);
            return 0;
        }
        if (parsed.Error is not null)
        {
            await WriteErrorAsync(parsed, parsed.Error, 2, stdout, stderr);
            return 2;
        }

        try
        {
            return parsed.Command switch
            {
                "capture" => await CaptureAsync(parsed, stdout, stderr, cancellationToken),
                "check" => await CheckAsync(parsed, stdout, stderr, cancellationToken),
                "diff" => await DiffAsync(parsed, stdout, stderr, cancellationToken),
                _ => await WriteErrorAsync(parsed, "A command is required. Use --help for usage.", 2, stdout, stderr)
            };
        }
        catch (ProjectAnalysisException exception)
        {
            return await WriteErrorAsync(parsed, exception.Message, 3, stdout, stderr, analysisError: true);
        }
        catch (ManifestReadException exception)
        {
            return await WriteErrorAsync(parsed, exception.Message, 3, stdout, stderr, analysisError: true);
        }
        catch (ManifestWriteException exception)
        {
            return await WriteErrorAsync(parsed, exception.Message, 3, stdout, stderr, analysisError: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return await WriteErrorAsync(parsed, "The project or solution could not be read.", 3, stdout, stderr, analysisError: true);
        }
        catch (OperationCanceledException)
        {
            return await WriteErrorAsync(parsed, "Analysis was cancelled.", 3, stdout, stderr, analysisError: true);
        }
    }

    private static async Task<int> CaptureAsync(ParsedArguments parsed, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var manifest = await LogSchemaExtractor.ExtractAsync(parsed.Positionals[0], parsed.TargetFramework, cancellationToken);
        var errors = manifest.AnalysisIssues.Where(issue => string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase)).Select(issue => issue.Code + ": " + issue.Message).Order(StringComparer.Ordinal).ToArray();
        if (errors.Length == 0)
        {
            await ManifestJson.WriteAsync(manifest, parsed.OutputPath!, cancellationToken);
        }

        var envelope = new CommandEnvelope(Array.Empty<string>(), errors, Array.Empty<CompatibilityFinding>(), errors.Length == 0 ? Path.GetFileName(parsed.OutputPath) : null, manifest.Events.Count, manifest.Unsupported.Count, manifest.Unsupported, manifest.Unsupported.Count == 0);
        await WriteEnvelopeAsync(parsed, envelope, stdout, stderr, errors.Length > 0 ? 3 : 0);
        return errors.Length > 0 ? 3 : 0;
    }

    private static async Task<int> CheckAsync(ParsedArguments parsed, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var current = await LogSchemaExtractor.ExtractAsync(parsed.Positionals[0], parsed.TargetFramework, cancellationToken);
        var baseline = await ManifestJson.ReadAsync(parsed.BaselinePath!, cancellationToken);
        var report = ComparisonEngine.Compare(baseline, current, parsed.Gate, parsed.AcceptedCodes, rejectEmptyEventSets: true);
        var unsupported = baseline.Unsupported.Concat(current.Unsupported).Distinct().ToArray();
        var envelope = new CommandEnvelope(Array.Empty<string>(), report.AnalysisErrors, report.Findings, null, current.Events.Count, unsupported.Length, unsupported, unsupported.Length == 0);
        var exitCode = report.AnalysisErrors.Count > 0 ? 3 : report.HasGatedFindings(parsed.Gate) ? 1 : 0;
        await WriteEnvelopeAsync(parsed, envelope, stdout, stderr, exitCode);
        return exitCode;
    }

    private static async Task<int> DiffAsync(ParsedArguments parsed, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var oldManifest = await ManifestJson.ReadAsync(parsed.Positionals[0], cancellationToken);
        var newManifest = await ManifestJson.ReadAsync(parsed.Positionals[1], cancellationToken);
        var report = ComparisonEngine.Compare(oldManifest, newManifest, parsed.Gate, parsed.AcceptedCodes);
        var unsupported = oldManifest.Unsupported.Concat(newManifest.Unsupported).Distinct().ToArray();
        var envelope = new CommandEnvelope(Array.Empty<string>(), report.AnalysisErrors, report.Findings, null, newManifest.Events.Count, unsupported.Length, unsupported, unsupported.Length == 0);
        var exitCode = report.AnalysisErrors.Count > 0 ? 3 : report.HasGatedFindings(parsed.Gate) ? 1 : 0;
        await WriteEnvelopeAsync(parsed, envelope, stdout, stderr, exitCode);
        return exitCode;
    }

    private static async Task<int> WriteErrorAsync(ParsedArguments parsed, string message, int exitCode, TextWriter stdout, TextWriter stderr, bool analysisError = false)
    {
        var envelope = analysisError
            ? new CommandEnvelope(Array.Empty<string>(), [message], Array.Empty<CompatibilityFinding>(), null, null, null, Array.Empty<UnsupportedDeclaration>(), false)
            : new CommandEnvelope([message], Array.Empty<string>(), Array.Empty<CompatibilityFinding>(), null, null, null, Array.Empty<UnsupportedDeclaration>(), false);
        await WriteEnvelopeAsync(parsed, envelope, stdout, stderr, exitCode);
        return exitCode;
    }

    private static async Task WriteEnvelopeAsync(ParsedArguments parsed, CommandEnvelope envelope, TextWriter stdout, TextWriter stderr, int exitCode)
    {
        if (parsed.Format == OutputFormat.Json)
        {
            await stdout.WriteLineAsync(JsonSerializer.Serialize(envelope, OutputOptions));
            return;
        }

        foreach (var error in envelope.ToolErrors)
        {
            await stderr.WriteLineAsync("ERROR " + error);
        }
        foreach (var error in envelope.AnalysisErrors)
        {
            await stderr.WriteLineAsync("ANALYSIS ERROR " + error);
        }
        foreach (var finding in envelope.Findings)
        {
            var accepted = finding.Accepted ? " ACCEPTED" : string.Empty;
            await stdout.WriteLineAsync($"{SeverityText(finding.Severity),-8} {finding.Code} {finding.Message}{accepted}");
        }
        if (envelope.Findings.Count == 0 && envelope.ToolErrors.Count == 0 && envelope.AnalysisErrors.Count == 0 && envelope.CoverageComplete)
        {
            await stdout.WriteLineAsync("LogSchema: no gated incompatibilities found.");
        }
        if (envelope.EventCount is not null)
        {
            await stdout.WriteLineAsync($"Events: {envelope.EventCount}");
        }
        if (envelope.ManifestPath is not null)
        {
            await stdout.WriteLineAsync($"Wrote canonical manifest: {envelope.ManifestPath}");
            await stdout.WriteLineAsync($"Unsupported declarations: {envelope.UnsupportedCount}");
        }
        if (envelope.Unsupported.Count > 0)
        {
            foreach (var item in envelope.Unsupported)
            {
                await stdout.WriteLineAsync($"UNSUPPORTED {item.ProjectKey} {item.DeclarationKey}: {item.Reason}");
            }
            await stdout.WriteLineAsync("Coverage: incomplete; unsupported declarations prevent a complete comparison.");
        }
        if (exitCode == 1)
        {
            await stdout.WriteLineAsync("LogSchema: gated compatibility findings were found.");
        }
    }

    private static string SeverityText(FindingSeverity severity) => severity switch
    {
        FindingSeverity.Breaking => "BREAKING",
        FindingSeverity.Warning => "WARNING",
        _ => "INFO"
    };

    private static ParsedArguments Parse(string[] args)
    {
        if (args.Any(argument => argument is "--help" or "-h")) return ParsedArguments.Help;
        if (args.Length == 0) return new(null, [], "A command is required. Use --help for usage.", DetectRequestedFormat(args), SeverityGate.Breaking, new HashSet<string>(StringComparer.Ordinal), null, null, null);
        var command = args[0].ToLowerInvariant();
        if (command is not ("capture" or "check" or "diff")) return new(command, [], "Unknown command. Use --help for usage.", DetectRequestedFormat(args), SeverityGate.Breaking, new HashSet<string>(StringComparer.Ordinal), null, null, null);

        var positional = new List<string>();
        var accepts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? output = null;
        string? baseline = null;
        string? tfm = null;
        var format = OutputFormat.Text;
        var gate = SeverityGate.Breaking;
        for (var index = 1; index < args.Length; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(argument);
                continue;
            }
            switch (argument)
            {
                case "--no-telemetry":
                    break;
                case "--output":
                    output = NextValue(args, ref index, argument);
                    break;
                case "--baseline":
                    baseline = NextValue(args, ref index, argument);
                    break;
                case "--format":
                    var formatValue = NextValue(args, ref index, argument);
                    if (formatValue.Equals("text", StringComparison.OrdinalIgnoreCase)) format = OutputFormat.Text;
                    else if (formatValue.Equals("json", StringComparison.OrdinalIgnoreCase)) format = OutputFormat.Json;
                    else return Invalid(command, "--format must be text or json.", format, gate);
                    break;
                case "--severity":
                    var severityValue = NextValue(args, ref index, argument);
                    gate = severityValue.ToLowerInvariant() switch { "breaking" => SeverityGate.Breaking, "warning" => SeverityGate.Warning, "all" => SeverityGate.All, _ => (SeverityGate)(-1) };
                    if ((int)gate < 0) return Invalid(command, "--severity must be breaking, warning, or all.", format, SeverityGate.Breaking);
                    break;
                case "--accept":
                    accepts.Add(NextValue(args, ref index, argument));
                    break;
                case "--tfm":
                    tfm = NextValue(args, ref index, argument);
                    break;
                default:
                    return Invalid(command, "Unknown option: " + argument, format, gate);
            }
        }

        if (command == "capture")
        {
            if (positional.Count != 1 || baseline is not null) return Invalid(command, "capture requires exactly one project or solution and does not accept --baseline.", format, gate);
            output ??= Path.Combine(Directory.GetCurrentDirectory(), "logschema.json");
        }
        else if (command == "check")
        {
            if (positional.Count != 1 || string.IsNullOrWhiteSpace(baseline) || output is not null) return Invalid(command, "check requires one project or solution and --baseline <file>.", format, gate);
        }
        else if (positional.Count != 2 || output is not null || baseline is not null || tfm is not null)
        {
            return Invalid(command, "diff requires exactly two manifest files.", format, gate);
        }

        return new(command, positional, null, format, gate, accepts, output, baseline, tfm);
    }

    private static ParsedArguments Invalid(string command, string error, OutputFormat format = OutputFormat.Text, SeverityGate gate = SeverityGate.Breaking) => new(command, [], error, format, gate, new HashSet<string>(StringComparer.Ordinal), null, null, null);

    private static OutputFormat DetectRequestedFormat(string[] args)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (args[index].Equals("--format", StringComparison.Ordinal) && args[index + 1].Equals("json", StringComparison.OrdinalIgnoreCase))
            {
                return OutputFormat.Json;
            }
        }

        return OutputFormat.Text;
    }

    private static string NextValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal)) throw new InvocationException(option + " requires a value.");
        return args[index];
    }

    private sealed record ParsedArguments(string? Command, IReadOnlyList<string> Positionals, string? Error, OutputFormat Format, SeverityGate Gate, IReadOnlySet<string> AcceptedCodes, string? OutputPath, string? BaselinePath, string? TargetFramework)
    {
        internal bool ShowHelp => ReferenceEquals(this, Help);
        internal static readonly ParsedArguments Help = new("help", [], null, OutputFormat.Text, SeverityGate.Breaking, new HashSet<string>(StringComparer.Ordinal), null, null, null);
    }
}

internal enum OutputFormat { Text, Json }

internal sealed record CommandEnvelope(
    [property: JsonPropertyName("toolErrors")] IReadOnlyList<string> ToolErrors,
    [property: JsonPropertyName("analysisErrors")] IReadOnlyList<string> AnalysisErrors,
    [property: JsonPropertyName("findings")] IReadOnlyList<CompatibilityFinding> Findings,
    [property: JsonPropertyName("manifestPath")] string? ManifestPath,
    [property: JsonPropertyName("eventCount")] int? EventCount,
    [property: JsonPropertyName("unsupportedCount")] int? UnsupportedCount,
    [property: JsonPropertyName("unsupported")] IReadOnlyList<UnsupportedDeclaration> Unsupported,
    [property: JsonPropertyName("coverageComplete")] bool CoverageComplete);

internal sealed class InvocationException(string message) : Exception(message);

internal static class CliHelp
{
    internal const string Text = """
LogSchema fails CI when a source-generated .NET logging contract changes incompatibly.

Usage:
  logschema capture <project-or-solution> [--output <file>]
  logschema check <project-or-solution> --baseline <file>
  logschema diff <old-manifest> <new-manifest>

Options:
  --format text|json       Choose human or machine-readable output.
  --severity breaking|warning|all
                           Set the exit-code gate; default is breaking.
  --accept <code>          Explicitly accept a diagnostic code without rewriting a baseline.
  --no-telemetry           Reserved v1 opt-out; v1 contains no telemetry client.
  --tfm <target-framework> Select a target framework for multi-targeted projects.
  --help                   Show this help.

Exit codes:
  0 analysis succeeded and no gated findings were found
  1 analysis succeeded and gated findings were found
  2 invalid invocation or configuration
  3 project-load or analysis failure, including zero supported events

Compatibility summary:
  BREAKING EventId/EventName changes and placeholder removal, rename, or order changes (default gate).
  WARNING  LogLevel changes (use --severity warning to gate).
  INFO     Event/placeholder additions and prose-only template changes with unchanged structured shape.
  Structured identity fields use exact ordinal comparison; case-only placeholder renames are KMLOG102.
  Malformed or contradictory manifest identity tuples fail analysis with exit 3 before comparison.
  KMLOGP006 means no supported [LoggerMessage] declarations were found; capture/check return 3 and
  capture does not write a baseline. diff remains a pure manifest comparison.
""";
}
