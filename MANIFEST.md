# Manifest schema v1

The complete analysis-diagnostic family is `KMLOGP001`, `KMLOGP002`, `KMLOGP003`, `KMLOGP004`, `KMLOGP005`, `KMLOGP006`, `KMLOGP007`, `KMLOGP008`, `KMLOGP009`, `KMLOGP010`, and `KMLOGP011`. The stable definitions and exit behavior are maintained in [COMPATIBILITY-RULES.md](COMPATIBILITY-RULES.md).

The default file is `logschema.json`. The top-level `schemaVersion` is an integer and must be `1`; the package version is independent. A future schema version is rejected rather than reinterpreted.

## Shape

- `projects`: project identity records with a stable key, project name, assembly name, and selected target framework.
- `events`: supported event records with project key, full method identity, containing type, method, generic arity, parameter ref kinds, effective EventId, effective EventName, effective level, message template, message-template placeholder occurrences, the generator-effective parameter and structured-state model, and logical source provenance. When EventId is omitted, the extractor records the pinned generator's deterministic non-randomized hash of the effective EventName. When EventName is omitted, the effective method name is recorded. An omitted level is recorded as `Dynamic` when a `LogLevel` parameter supplies it; an explicit `LogLevel.None` remains `None`.
- `unsupported`: every discovered but unsupported declaration, including normalized declaration text, identity, source provenance, and reason.
- `analysisIssues`: explicit warnings/errors such as ambiguous or unpaired source/generated identity, source-provenance collisions, workspace failures, an untrustworthy compilation, an unverified package/generator pair (`KMLOGP009`), mixed generator versions across a solution (`KMLOGP010`), an unpairable generator run diagnostic (`KMLOGP011`), or `KMLOGP006` when no supported `[LoggerMessage]` declarations were found.
- `compilationDiagnosticKinds` and `workspaceDiagnosticKinds`: sorted diagnostic categories without source paths or raw machine-specific messages. All seven top-level fields are required, including empty diagnostic arrays.

New captures also include `integrity`, an uppercase SHA-256 digest of the canonical manifest with that field omitted; a present digest is verified before comparison. Legacy v1 inputs without `integrity` remain readable only after the semantic reader checks below pass.

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
- `parameters` is the method-parameter list in declaration order. Each record contains the source parameter name, canonical declared type, ref kind, and generator-effective `role`: `Logger`, `Exception`, `LogLevel`, or `State`. `LogLevel` identifies the first applicable candidate; whether it supplies the runtime level is represented separately by `levelSource`.
- `structuredState` is the unique emitted state-property list in method-parameter order. Each record identifies its source `parameterName` and emitted `emittedName`. A matched ordinary placeholder supplies emitted-name casing; an ordinary parameter with no occurrence still emits a property named after the parameter. A fixed-level first `LogLevel` parameter is ordinary emitted state even when it is absent from the template; the dynamic first `LogLevel` parameter supplies the level and is excluded. A referenced first special exception is also emitted as state according to the generator's special-parameter behavior.
- `loggerParameter` identifies only the first applicable `ILogger` parameter. Later `ILogger` candidates have role `State`.
- `exceptionParameter` identifies only the first applicable exception parameter. Later exception-derived candidates have role `State`.
- `levelSource` is `Fixed` for an attribute level and `Dynamic` when the first applicable `LogLevel` parameter supplies the level. `levelParameter` identifies the first applicable `LogLevel` candidate for either source; later `LogLevel` candidates are `State`.

The reader requires level cross-field coherence: `Fixed` accepts only a fixed level and records the first `LogLevel` candidate (or null when none exists), while `Dynamic` requires the `Dynamic` level and that same first candidate. Unknown, mixed, missing, late, and fixed-plus-dynamic combinations fail before comparison. It also reparses `message` and requires `placeholders` to match every occurrence exactly, including token text, name casing, whitespace, `@`, alignment, format delimiters, Unicode, escaped braces, repetition, and order.

The first-special rules are semantic and parameter-driven, not occurrence-driven. A placeholder matching `loggerParameter` or a dynamic `levelParameter` is outside the supported v1 declaration scope and is retained in `unsupported` so `check` and `diff` fail closed. A placeholder matching a fixed-level `levelParameter` is ordinary generator-emitted state; a placeholder matching `exceptionParameter` is supported with both exception handling and its generator-emitted state property. The extractor runs the resolved pinned generator through `GeneratorDriver`, pairs each `GetRunResult()` diagnostic with its source declaration, and treats only `SYSLIB1015` as benign; other diagnostic-bearing declarations remain unsupported. Template parsing follows the pinned generator's odd/even brace-run behavior: escaped `{{` and `}}` are skipped, an unmatched opening or closing brace is rejected, and placeholder names are trimmed only for semantic matching while the occurrence text preserves casing, whitespace, `@` prefixes, alignment, format delimiters, and Unicode. A source declaration is supported only when exactly one implementation from that run has the same declaration identity and a symbol-resolved `GeneratedCodeAttribute` with the pinned tool/version values; custom-generator and user-authored implementations remain unsupported. The resolved project pairs are `Microsoft.Extensions.Logging.Abstractions` 10.0.1 with `Microsoft.Extensions.Logging.Generators` 10.0.13.7005 and `Microsoft.Extensions.Logging.Abstractions` 10.0.12 with `Microsoft.Extensions.Logging.Generators` 10.0.14.42308. Extraction fails closed for absent, unknown, unsupported, or cross-pair versions, and for mixed generator versions anywhere in one solution (`KMLOGP010`).

`parameterRefKinds` and `parameterForms` are redundant positional projections of the canonical identity. Each must have exactly one value per identity parameter. The reader parses and canonicality-checks every type from the identity, recomputes the required form vector structurally with the table above, and requires both the embedded forms and `parameterForms` to equal it at every position. The parsed containing type, method, generic arity, parameter count, ref kinds, and recomputed forms must match exactly; additive, subtractive, or substituted redundant values are analysis errors. No supplied form for an arbitrary custom type is accepted.

Comparison matches events by project key and the form-neutral declared method signature: containing type, method, generic arity, ref kinds, and canonical types. Reader-accepted manifests with the same declared signature necessarily have the same recomputed forms. Comparison retains a defense-in-depth analysis error if invalid in-memory inputs disagree on forms instead of treating the disagreement as an event removal/addition. Parameter names and generator roles remain part of the persisted effective state model even though names are not part of the form-neutral event identity.

The serializer normalizes line endings in declaration text and JSON output. V1 contains no telemetry client, so source and manifest content remain local. Source provenance is logical and never includes an absolute path or private checkout root. The same source and selected target framework therefore produce byte-identical output regardless of checkout/output directory, line-ending checkout, or host path separator.

## Persisted-field integrity audit

The read path applies the following bounded checks before comparison. Newly captured manifests additionally cover every persisted value with `integrity`, so a field added to the schema cannot silently bypass the generic mutation guard.

| Persisted field family | Read-time evidence |
| --- | --- |
| `schemaVersion` | Exact supported value `1`. |
| Project key, assembly, and target framework | Key must be exactly `<assembly>|<target-framework>`, unique, and referenced by every event, unsupported record, and analysis issue. |
| Event identity and redundant identity fields | Roslyn parsing and canonical reserialization authenticate containing type, method, generic arity, parameter count, ref kinds, canonical types, and comparison identity. |
| `parameterForms` and embedded identity forms | Required forms are recomputed from every canonical declared type and matched positionally. |
| Parameter names, types, ref kinds, and roles | Names/types/ref kinds are matched to the parsed identity; roles and `loggerParameter`/`exceptionParameter`/`levelParameter` are checked through the generator-effective first-special model. |
| `level`, `levelSource`, and `levelParameter` | Fixed/dynamic source, fixed-vs-dynamic level, first candidate, missing candidate, late candidate, and unknown values are checked as one cross-field tuple. |
| `message` and `placeholders` | The pinned brace parser recomputes the full occurrence sequence and requires exact token, name, casing, whitespace, delimiter, Unicode, repetition, and order equality. |
| `structuredState` names and order | State membership is recomputed from parameter roles, dynamic-level exclusion, and placeholder casing; parameter order, uniqueness, and emitted names are matched. |
| `eventId`, `eventName`, and raw message/source facts | These are source attribute results rather than values derivable from the manifest's other fields; a newly captured manifest's integrity digest authenticates their captured bytes. |
| `unsupported`, `analysisIssues`, diagnostic-kind lists, and source-provenance fields | Project ownership, vocabulary, severity, source path safety, source kind, and bounds are checked; the integrity digest covers their complete persisted records and prevents individually forged mutations in new captures. |
| `integrity` | Must be an uppercase 64-hex SHA-256 digest equal to the canonical serialization with `integrity` omitted. |

## Parsing safety and compatibility

The parser and writer use the same 4 MiB UTF-8 resource limit and JSON nesting depth 32. The reader rejects malformed JSON, comments, trailing commas, incomplete required fields, null records, malformed or contradictory identity tuples, unknown ref-kind or semantic-form values, non-canonical declared type text (including `global::`, `@`-escaped identifiers, trivia/comments, or non-canonical generic spelling), any embedded or redundant form that differs from the reader-recomputed type function, incoherent level fields, placeholder occurrences that differ from `message`, non-canonical source paths, unrelated project references, oversized record arrays, any schema version other than 1, and any present `integrity` digest that differs from canonical contents. A failed parse is an analysis failure and returns exit code 3. Capture validates a serialized round trip and writes through a temporary file before replacing the destination, so a failed capture does not truncate an existing manifest. Baselines are never changed by `check` or `diff`.

Every read-time or comparison-time analysis error uses the same fail-closed result contract: exit code 3, an empty `findings` array, and `coverageComplete: false`. Counts remain available when comparison reached analyzed manifests; they are `null` when an input could not be read far enough to establish them. Text output uses `ANALYSIS ERROR`; neither format emits a stack trace or absolute input path.

Project capture and check use one analysis budget: at most 64 C# projects, 512 source documents per project, 4,096 source documents total, 1 MiB per source document, 16 MiB source per project, 64 MiB source total, 8,192 compilation syntax trees per project, 4,096 generated trees per project, 4 MiB generated text per tree, 64 MiB generated text total, and 4,096 discovered declarations, supported events, or unsupported declarations. `.sln` and `.slnx` project entries are counted in a bounded preflight before the full solution-open path where their format permits. An exceeded ceiling is a `Project analysis resource limit exceeded` analysis error: exit 3, no baseline, no stack trace, and no machine path. These semantic-work ceilings do not sandbox or absolutely bound MSBuild evaluation, restore, SDK behavior, or generator execution while inputs are being produced; callers must isolate untrusted projects.

An analysis that discovers zero supported events reports `KMLOGP006` and returns exit code 3. `capture` does not write a zero-event baseline. `check` rejects both a zero-event current analysis and a baseline manifest with zero events; `diff` remains a pure comparison of the two manifests and does not apply this project-analysis rule.

Unsupported declarations are retained with their identities and reasons. `check` and `diff` report incomplete coverage as analysis diagnostic `KMLOGP007` and return exit code 3 rather than presenting the supported subset as a complete comparison.

## Trust boundary

Capture uses design-time MSBuild/Roslyn semantic loading. It does not use `Assembly.Load`, invoke target methods, or inspect runtime logging. MSBuild project evaluation remains a local-machine trust boundary; project restore, local-tool restore, and SDK installation occur before normal offline analysis. The tool requires the .NET 8 runtime and project loading is verified with .NET SDK `10.0.401`.
