# Manifest schema v1

The default file is `logschema.json`. The top-level `schemaVersion` is an integer and must be `1`; the package version is independent. A future schema version is rejected rather than reinterpreted.

## Shape

- `projects`: project identity records with a stable key, project name, assembly name, and selected target framework.
- `events`: supported event records with project key, full method identity, containing type, method, generic arity, parameter ref kinds, effective EventId, effective EventName, effective level, message template, ordered placeholders, special parameter forms, and project-relative source provenance. When EventId is omitted, the extractor records the pinned generator's deterministic non-randomized hash of the effective EventName. When EventName is omitted, the effective method name is recorded. An omitted level is recorded as `Dynamic` when a `LogLevel` parameter supplies it; an explicit `LogLevel.None` remains `None`.
- `unsupported`: every discovered but unsupported declaration, including normalized declaration text, identity, source provenance, and reason.
- `analysisIssues`: explicit warnings/errors such as ambiguous identity, unpaired generated declarations, workspace failures, an untrustworthy compilation, or `KMLOGP006` when no supported `[LoggerMessage]` declarations were found.
- `compilationDiagnosticKinds` and `workspaceDiagnosticKinds`: sorted diagnostic categories without source paths or raw machine-specific messages. All seven top-level fields are required, including empty diagnostic arrays.

## Canonicalization

Manifests are UTF-8 without a BOM, use invariant culture for numeric and enum values, and end with one LF. Every list is sorted with ordinal comparison: projects by key; events by project key then identity; unsupported declarations by project key, source file, line, and declaration key; analysis issues by project key, code, and declaration key; diagnostic-kind lists ordinally. Project source paths are relative to the project directory, use `/`, and generated paths use `generated/<file-name>`. Absolute paths, drive letters, and backslashes are not emitted.

A v1 method identity has the exact form `<containing-type>.<method>\`<generic-arity>(<parameters>)`. Each parameter is `<ref-kind>:<semantic-form>:<canonical-type>`, and parameters are comma-separated outside nested generic, array, tuple, or function-pointer type syntax. The ref-kind vocabulary is `None`, `Ref`, `Out`, `In`, `RefReadOnly`, and `RefReadOnlyParameter`. The semantic-form vocabulary is `None`, `Exception`, `ILogger`, and `LogLevel`.

The form is a pure function of the canonical declared parameter type. It is not trusted semantic evidence supplied by the manifest:

| Canonical declared parameter type | Required form |
| --- | --- |
| `Microsoft.Extensions.Logging.ILogger` (including constructed logger types) | `ILogger` |
| exact `Microsoft.Extensions.Logging.LogLevel` | `LogLevel` |
| exact `System.Exception` | `Exception` |
| every other type, including any type that derives from `System.Exception` | `None` |

V1 does not assert semantic base-type classification in the manifest. A non-suffix derived exception such as `DerivedProblem` is recorded as `None:DerivedProblem`, while exact `System.Exception` is `Exception:System.Exception`. Capture still uses semantic base-type detection to exclude exception parameters from the message placeholder list; that extraction rule does not alter the manifest form. Events with derived exception parameters remain supported and round-trip normally.

`parameterRefKinds` and `parameterForms` are redundant positional projections of the canonical identity. Each must have exactly one value per identity parameter. The reader parses the canonical types from the identity, recomputes the required form vector with the table above, and requires both the embedded forms and `parameterForms` to equal it at every position. The parsed containing type, method, generic arity, parameter count, ref kinds, and recomputed forms must match exactly; additive, subtractive, or substituted redundant values are analysis errors. No supplied form for an arbitrary custom type is accepted.

Comparison matches events by project key and the form-neutral declared method signature: containing type, method, generic arity, ref kinds, and canonical types. Reader-accepted manifests with the same declared signature necessarily have the same recomputed forms. Comparison retains a defense-in-depth analysis error if invalid in-memory inputs disagree on forms instead of treating the disagreement as an event removal/addition.

The serializer normalizes line endings in declaration text and JSON output. V1 contains no telemetry client, so source and manifest content remain local. Source provenance is project-relative and is never used to emit an absolute path. The same source and selected target framework therefore produce byte-identical output regardless of checkout/output directory, line-ending checkout, or host path separator.

## Parsing safety and compatibility

The parser and writer use the same 4 MiB UTF-8 resource limit and JSON nesting depth 32. The reader rejects malformed JSON, comments, trailing commas, incomplete required fields, null records, malformed or contradictory identity tuples, unknown ref-kind or semantic-form values, any embedded or redundant form that differs from the reader-recomputed type function, non-canonical source paths, unrelated project references, oversized record arrays, and any schema version other than 1. A failed parse is an analysis failure and returns exit code 3. Capture validates a serialized round trip and writes through a temporary file before replacing the destination, so a failed capture does not truncate an existing manifest. Baselines are never changed by `check` or `diff`.

Every read-time or comparison-time analysis error uses the same fail-closed result contract: exit code 3, an empty `findings` array, and `coverageComplete: false`. Counts remain available when comparison reached analyzed manifests; they are `null` when an input could not be read far enough to establish them. Text output uses `ANALYSIS ERROR`; neither format emits a stack trace or absolute input path.

An analysis that discovers zero supported events reports `KMLOGP006` and returns exit code 3. `capture` does not write a zero-event baseline. `check` rejects both a zero-event current analysis and a baseline manifest with zero events; `diff` remains a pure comparison of the two manifests and does not apply this project-analysis rule.

Unsupported declarations are retained with their identities and reasons. `check` and `diff` report incomplete coverage as analysis diagnostic `KMLOGP007` and return exit code 3 rather than presenting the supported subset as a complete comparison.

## Trust boundary

Capture uses design-time MSBuild/Roslyn semantic loading. It does not use `Assembly.Load`, invoke target methods, or inspect runtime logging. MSBuild project evaluation remains a local-machine trust boundary; project restore, local-tool restore, and SDK installation occur before normal offline analysis. The tool requires the .NET 8 runtime and project loading is verified with .NET SDK `10.0.401`.
