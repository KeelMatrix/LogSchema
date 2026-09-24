# Compatibility rules

This file is the single source of truth for v1 change classification and diagnostic codes. A comparison matches events by canonical project key and the form-neutral declared method signature (containing type, method, generic arity, ref kinds, and canonical types). A declared signature change therefore appears as one removed event and one added event; removal is the breaking side of that change. A semantic-form change for an otherwise identical signature is an analysis error because comparison coverage is no longer trustworthy.

Structured identity fields use exact ordinal semantics: comparisons are case-sensitive and culture-invariant. This applies to canonical project and method identities, EventName, and structured-state property names and order. A case-only emitted-property change is therefore a structured-state rename (`KMLOG102`), not template-only drift. `KMLOG301` applies only when the effective structured state is unchanged under these ordinal comparisons.

| Code | Change | Severity | Gated by default | Notes |
| --- | --- | --- | --- | --- |
| KMLOG001 | Event added | INFO | No | Additions are visible but compatible. |
| KMLOG002 | Event removed | BREAKING | Yes | Includes the removed side of an identity change. |
| KMLOG003 | EventId changed | BREAKING | Yes | The logical event identity changed. |
| KMLOG004 | EventName changed | BREAKING | Yes | Includes a change from or to the framework default method name. |
| KMLOG101 | Structured-state property removed | BREAKING | Yes | Removing an emitted structured property breaks consumers. |
| KMLOG102 | Structured-state property renamed | BREAKING | Yes | A one-for-one removed/added emitted-name pair is reported as a rename. |
| KMLOG103 | Structured-state property order changed | BREAKING | Yes | Method-parameter order is part of the supported source-generated state shape. |
| KMLOG104 | Structured-state property added | INFO | No | Additions are visible but compatible. |
| KMLOG105 | Generator-effective parameter role changed | BREAKING | Yes | A logger, exception, dynamic-level, or ordinary state role changed. |
| KMLOG201 | LogLevel changed | WARNING | No | Use `--severity warning` to gate warnings. |
| KMLOG301 | Message template changed with unchanged structured state | INFO | No | Includes prose, occurrence, and formatting-token changes that do not change emitted state. |

`--severity breaking` gates only BREAKING findings. `--severity warning` gates BREAKING and WARNING findings. `--severity all` gates every finding, including INFO. Findings remain structured and stable in text and JSON output. `--accept <code>` explicitly accepts matching findings for the current comparison and never rewrites a baseline.

## Analysis diagnostics

Analysis diagnostics make an extracted or compared contract untrustworthy. An error returns exit code 3. Warnings remain in a captured manifest for review; comparison rejects any manifest with unsupported declarations through `KMLOGP007`.

Manifest read validation runs before comparison. Each canonical identity parameter embeds a type form beside its ref kind and canonical declared type, while each event also stores the source-derived parameter role and effective structured state. The reader parses each declared type with Roslyn and requires byte-for-byte canonical text: namespace-qualified syntax with `global::` removed, unescaped identifiers, no trivia/comments, and canonical generic spelling. Non-canonical type text is rejected. It then classifies the type form structurally: `ILogger` only for exactly top-level `Microsoft.Extensions.Logging.ILogger` arity 0 or exactly top-level `Microsoft.Extensions.Logging.ILogger<T>` arity 1; `LogLevel` only for exact top-level `Microsoft.Extensions.Logging.LogLevel` arity 0; `Exception` only for exact top-level `System.Exception` arity 0; and `None` for every other top-level canonical type, including wrong-arity or generic-rooted nested `ILogger` members and derived exception types. Raw prefixes and suffixes are never used. Both the embedded form and the redundant `parameterForms` position must equal the recomputed value. The source-derived roles are validated for completeness, first-special uniqueness, parameter names, and method-order structured-state membership; they are not recomputed from untrusted manifest type text. Malformed or non-canonical method identities, any additive, subtractive, or substituted difference between a parsed identity and its redundant containing type, method, generic arity, parameter count, ref kinds, or positional parameter forms, and unknown vocabulary are analysis errors. Comparison also retains a defense-in-depth analysis error for form disagreement on one form-neutral declared signature. Text output uses the `ANALYSIS ERROR` envelope; JSON places the message in `analysisErrors`, leaves `findings` empty, sets `coverageComplete` to `false`, and returns exit code 3 for both read-time and comparison-time analysis errors.

| Code | Condition | Severity and result |
| --- | --- | --- |
| KMLOGP001 | Multiple project-source `[LoggerMessage]` declarations share one stable method identity, so generated pairing is ambiguous. | Error; capture/check return exit 3 and no baseline is written. |
| KMLOGP002 | A generated `[LoggerMessage]` declaration has no one-to-one project-source counterpart. | Warning in the captured analysis; the declaration is retained in `unsupported`. A later check/diff reports `KMLOGP007`. |
| KMLOGP003 | Multiple loaded projects share one canonical `<assembly>|<target-framework>` project key. | Error; capture/check return exit 3 because event ownership is ambiguous. |
| KMLOGP004 | MSBuild reports a workspace failure while loading the project graph. | Error; capture/check return exit 3 because project evaluation is incomplete. |
| KMLOGP005 | The project compilation contains an error other than the expected design-time unimplemented-partial diagnostic. | Error; capture/check return exit 3 because the extracted contract is not trustworthy. |
| KMLOGP006 | No supported `[LoggerMessage]` declarations were found. | `capture` and `check` return exit 3; `capture` writes no baseline and `check` rejects a zero-event baseline. |
| KMLOGP007 | The manifest contains unsupported declarations. | `check` and `diff` return exit 3 and report the unsupported declaration identities and reasons; the supported subset is not presented as complete coverage. |

Rules apply to C# methods using `Microsoft.Extensions.Logging.LoggerMessageAttribute` within the documented v1 supported declaration scope. Manual logging calls, runtime values, providers, and arbitrary third-party generators are not compared.
