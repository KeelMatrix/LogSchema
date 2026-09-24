# Manifest schema v1

The default file is `logschema.json`. The top-level `schemaVersion` is an integer and must be `1`; the package version is independent. A future schema version is rejected rather than reinterpreted.

## Shape

- `projects`: project identity records with a stable key, project name, assembly name, and selected target framework.
- `events`: supported event records with project key, full method identity, containing type, method, generic arity, parameter ref kinds, effective EventId, effective EventName, effective level, message template, message-template placeholder occurrences, the generator-effective parameter and structured-state model, and logical source provenance. When EventId is omitted, the extractor records the pinned generator's deterministic non-randomized hash of the effective EventName. When EventName is omitted, the effective method name is recorded. An omitted level is recorded as `Dynamic` when a `LogLevel` parameter supplies it; an explicit `LogLevel.None` remains `None`.
- `unsupported`: every discovered but unsupported declaration, including normalized declaration text, identity, source provenance, and reason.
- `analysisIssues`: explicit warnings/errors such as ambiguous identity, unpaired generated declarations, source-provenance collisions, workspace failures, an untrustworthy compilation, or `KMLOGP006` when no supported `[LoggerMessage]` declarations were found.
- `compilationDiagnosticKinds` and `workspaceDiagnosticKinds`: sorted diagnostic categories without source paths or raw machine-specific messages. All seven top-level fields are required, including empty diagnostic arrays.

## Canonicalization

Manifests are UTF-8 without a BOM, use invariant culture for numeric and enum values, and end with one LF. Every list is sorted with ordinal comparison: projects by key; events by project key then identity; unsupported declarations by project key, source file, line, and declaration key; analysis issues by project key, code, and declaration key; diagnostic-kind lists ordinally. Source provenance is a deterministic logical identity, not sanitized path text:

- project documents use `project/<project-relative-path>`;
- documents physically outside the project root use `external/up-<N>/<relative-tail>`, where `N` is the number of parent traversals from the project root;
- generated trees use `generated/<generator-identity>/<hint-name>` from the stable path below Roslyn's `generated` directory, or `generated/content-<sha256>` when only stable generated content is available.

Meaningful dot-prefixed names are preserved. `/` is the only separator. Absolute paths, drive letters, traversal segments, and backslashes are never emitted. A source document without a safe stable identity, or distinct syntax trees that resolve to one logical identity, fails analysis with `KMLOGP008` rather than silently sharing provenance.

A v1 method identity has the exact form `<containing-type>.<method>\`<generic-arity>(<parameters>)`. Each parameter is `<ref-kind>:<semantic-form>:<canonical-type>`, and parameters are comma-separated outside nested generic, array, tuple, or function-pointer type syntax. The ref-kind vocabulary is `None`, `Ref`, `Out`, `In`, `RefReadOnly`, and `RefReadOnlyParameter`. The semantic-form vocabulary is `None`, `Exception`, `ILogger`, and `LogLevel`.

The declared type text must already be canonical. The reader parses it with Roslyn and requires byte-for-byte equality with the schema's canonical rendering: namespace-qualified syntax, no `global::` alias, no escaped `@` identifiers, no trivia or comments, and the canonical generic spelling. Non-canonical text is an analysis error; the reader does not normalize it and continue. The `semantic-form` value is a type-shape projection for identity compatibility, not the generator parameter role. The form is classified structurally from the parsed type, never from raw string prefixes or suffixes:

| Canonical declared parameter type | Required form |
| --- | --- |
| exactly `Microsoft.Extensions.Logging.ILogger` with arity 0 | `ILogger` |
| exactly top-level `Microsoft.Extensions.Logging.ILogger<T>` with arity 1 and one type argument | `ILogger` |
| exactly top-level `Microsoft.Extensions.Logging.LogLevel` with arity 0 | `LogLevel` |
| exactly top-level `System.Exception` with arity 0 | `Exception` |
| every other top-level canonical type, including wrong-arity or generic-rooted nested `ILogger` members and types that derive from `System.Exception` | `None` |

V1 does not assert semantic base-type classification in the identity form. A non-suffix derived exception such as `DerivedProblem` is recorded as `None:DerivedProblem`, while exact `System.Exception` is `Exception:System.Exception`. Capture separately performs source-semantic analysis to assign the generator-effective parameter role. That source-derived role is persisted and is not discarded merely because an offline manifest reader cannot reconstruct an arbitrary custom type hierarchy. Events with derived exception parameters remain supported and round-trip normally.

## Generator-effective logging model

The pinned Microsoft.Extensions.Logging source generator is the contract authority for the fields below:

- A `placeholder` is one occurrence parsed from the message template. It preserves occurrence order, spelling/casing, and format text. Repeating a placeholder creates repeated occurrences, not repeated state properties.
- `parameters` is the method-parameter list in declaration order. Each record contains the source parameter name, canonical declared type, ref kind, and generator-effective `role`: `Logger`, `Exception`, `DynamicLevel`, or `State`.
- `structuredState` is the unique emitted state-property list in method-parameter order. Each record identifies its source `parameterName` and emitted `emittedName`. A matched ordinary placeholder supplies emitted-name casing; an ordinary parameter with no occurrence still emits a property named after the parameter. A referenced first special exception is also emitted as state according to the generator's special-parameter behavior.
- `loggerParameter` identifies only the first applicable `ILogger` parameter. Later `ILogger` candidates have role `State`.
- `exceptionParameter` identifies only the first applicable exception parameter. Later exception-derived candidates have role `State`.
- `levelSource` is `Fixed` for an attribute level and `Dynamic` when the first applicable `LogLevel` parameter supplies the level. `levelParameter` identifies that first dynamic parameter; later `LogLevel` candidates are `State`, and a fixed-level `LogLevel` parameter is also `State`.

The first-special rules are semantic and parameter-driven, not occurrence-driven. A placeholder matching `loggerParameter` or `levelParameter` is outside the supported v1 declaration scope and is retained in `unsupported` so `check` and `diff` fail closed. A placeholder matching `exceptionParameter` is supported with both exception handling and its generator-emitted state property. Generator diagnostics such as an absent ordinary parameter warning do not erase the source-derived state model.

`parameterRefKinds` and `parameterForms` are redundant positional projections of the canonical identity. Each must have exactly one value per identity parameter. The reader parses and canonicality-checks every type from the identity, recomputes the required form vector structurally with the table above, and requires both the embedded forms and `parameterForms` to equal it at every position. The parsed containing type, method, generic arity, parameter count, ref kinds, and recomputed forms must match exactly; additive, subtractive, or substituted redundant values are analysis errors. No supplied form for an arbitrary custom type is accepted.

Comparison matches events by project key and the form-neutral declared method signature: containing type, method, generic arity, ref kinds, and canonical types. Reader-accepted manifests with the same declared signature necessarily have the same recomputed forms. Comparison retains a defense-in-depth analysis error if invalid in-memory inputs disagree on forms instead of treating the disagreement as an event removal/addition. Parameter names and generator roles remain part of the persisted effective state model even though names are not part of the form-neutral event identity.

The serializer normalizes line endings in declaration text and JSON output. V1 contains no telemetry client, so source and manifest content remain local. Source provenance is logical and never includes an absolute path or private checkout root. The same source and selected target framework therefore produce byte-identical output regardless of checkout/output directory, line-ending checkout, or host path separator.

## Parsing safety and compatibility

The parser and writer use the same 4 MiB UTF-8 resource limit and JSON nesting depth 32. The reader rejects malformed JSON, comments, trailing commas, incomplete required fields, null records, malformed or contradictory identity tuples, unknown ref-kind or semantic-form values, non-canonical declared type text (including `global::`, `@`-escaped identifiers, trivia/comments, or non-canonical generic spelling), any embedded or redundant form that differs from the reader-recomputed type function, non-canonical source paths, unrelated project references, oversized record arrays, and any schema version other than 1. A failed parse is an analysis failure and returns exit code 3. Capture validates a serialized round trip and writes through a temporary file before replacing the destination, so a failed capture does not truncate an existing manifest. Baselines are never changed by `check` or `diff`.

Every read-time or comparison-time analysis error uses the same fail-closed result contract: exit code 3, an empty `findings` array, and `coverageComplete: false`. Counts remain available when comparison reached analyzed manifests; they are `null` when an input could not be read far enough to establish them. Text output uses `ANALYSIS ERROR`; neither format emits a stack trace or absolute input path.

An analysis that discovers zero supported events reports `KMLOGP006` and returns exit code 3. `capture` does not write a zero-event baseline. `check` rejects both a zero-event current analysis and a baseline manifest with zero events; `diff` remains a pure comparison of the two manifests and does not apply this project-analysis rule.

Unsupported declarations are retained with their identities and reasons. `check` and `diff` report incomplete coverage as analysis diagnostic `KMLOGP007` and return exit code 3 rather than presenting the supported subset as a complete comparison.

## Trust boundary

Capture uses design-time MSBuild/Roslyn semantic loading. It does not use `Assembly.Load`, invoke target methods, or inspect runtime logging. MSBuild project evaluation remains a local-machine trust boundary; project restore, local-tool restore, and SDK installation occur before normal offline analysis. The tool requires the .NET 8 runtime and project loading is verified with .NET SDK `10.0.401`.
