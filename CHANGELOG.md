# Changelog

Changes for the KeelMatrix.LogSchema package are recorded here using the Keep a Changelog format.

## [Unreleased]

### Fixed

- Match emitted structured-state names to the pinned generator's complete `LoggerMessage.Define` selection predicate, including dynamic levels, generic methods, maximum arity, occurrence count, and parameter order after leading `@` removal.
- Derive Define eligibility and emitted state from the generator-effective template-parameter list; preserve fixed-level `LogLevel` state parameters emitted by both supported generator/package pairs while excluding a dynamic level parameter.

## [0.1.0] - Planned

### Added

- Deterministic schema-v1 manifests for supported source-generated `LoggerMessage` declarations, with fail-closed canonical event-identity validation.
- Reader-computed positional parameter forms derived only from canonical declared types, with exact `System.Exception` distinguished from supported derived exception parameters and fail-closed validation of embedded and redundant values.
- Canonical declared-type spelling enforcement and structural form classification, including fail-closed handling for escaped identifiers, trivia, wrong-arity logger types, and generic-rooted nested members.
- Offline `capture`, `check`, and `diff` commands with text and JSON diagnostics and CI-friendly exit codes.
- Stable compatibility classification for event, identity, structured-placeholder, level, and template changes.
- Explicit unsupported-declaration and analysis-error reporting without target application execution.
- A net8.0 .NET tool package with the `logschema` command, package-local README, exact archive-content validation, and portable symbols mapped through SourceLink to the canonical repository commit without private machine paths.
- An isolated package-consumer smoke path covering installation, help, capture, clean checks, breaking diagnostics, and invalid configuration.
- Exact ordinal structured-identity comparison, including case-only emitted structured-state renames as breaking `KMLOG102` findings.
- Fail-closed `KMLOGP006` handling for zero supported events, with no baseline written by `capture` and zero-event baselines rejected by `check`.
- Byte-deterministic `.nupkg` and `.snupkg` output across canonical-repository attached, detached, and alternate-directory checkouts, plus repeated-pack validation and an LF line-ending checkout contract.
- Repository-pinned local tool installation and restore instructions, with installed `capture`, `check`, and `diff` validation on Windows, Linux, and macOS.
- Collision-safe logical source provenance for project, linked outside-project, and generated documents, with fail-closed handling for unsafe or ambiguous identities.
- Bounded design-time project analysis with clear failures for oversized project and source graphs, including a reproducible resource gate for large SDK-style solutions.
- One coherent project-analysis budget covering solution preflight, source document counts and bytes, compilation/generated syntax trees and text, and incremental LoggerMessage declaration/event/unsupported counts, with actionable exit-3 failures and explicit MSBuild trust-boundary wording.
- Generator-grounded `LoggerMessage` semantics for the exact resolved `Microsoft.Extensions.Logging.Abstractions` 10.0.1 / generator assembly 10.0.13.7005 and 10.0.12 / generator assembly 10.0.14.42308 pairs, including independent first-special-role classification, `GeneratorDriver.GetRunResult()` diagnostics, symbol-bound source/generated pairing, generator-equivalent template parsing, and fail-closed project and solution version detection.
- Fail-closed persisted-manifest integrity: captured manifests validate their canonical contents before comparison, level-source fields are cross-validated, and placeholder occurrences are recomputed from the message before comparison.
- Generator-path-emitted state fidelity for escaped source parameter names and raw `@` placeholder spellings, using fixed/dynamic level, generic-method, six-parameter arity, occurrence-count, and ordered-name conditions to select `LoggerMessage.Define`; differential-oracle coverage spans both supported package/generator pairs.
- Fail-closed manifest validation for duplicate decoded properties, known built-in type/role contradictions, one canonical representation per effective level, and evaluated-project target-framework identity.
- Per-project generated-tree budget lifecycle, pre-conversion generated-text bounds, and duplicate generator/compiler observation accounting.
