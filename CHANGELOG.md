# Changelog

Changes for the KeelMatrix.LogSchema package are recorded here using the Keep a Changelog format.

## [Unreleased]

### Added

- Deterministic schema-v1 manifests for supported source-generated `LoggerMessage` declarations, with fail-closed canonical event-identity validation.
- Reader-computed positional parameter forms derived only from canonical declared types, with exact `System.Exception` distinguished from supported derived exception parameters and fail-closed validation of embedded and redundant values.
- Canonical declared-type spelling enforcement and structural form classification, including fail-closed handling for escaped identifiers, trivia, wrong-arity logger types, and generic-rooted nested members.
- Offline `capture`, `check`, and `diff` commands with text and JSON diagnostics and CI-friendly exit codes.
- Stable compatibility classification for event, identity, structured-placeholder, level, and template changes.
- Explicit unsupported-declaration and analysis-error reporting without target application execution.
- A net8.0 .NET tool package with the `logschema` command, package-local README, exact archive-content validation, and portable symbols mapped through SourceLink to the canonical repository commit without private machine paths.
- An isolated package-consumer smoke path covering installation, help, capture, clean checks, breaking diagnostics, and invalid configuration.
- Exact ordinal structured-identity comparison, including case-only placeholder renames as breaking `KMLOG102` findings.
- Fail-closed `KMLOGP006` handling for zero supported events, with no baseline written by `capture` and zero-event baselines rejected by `check`.
- Byte-deterministic `.nupkg` and `.snupkg` output across canonical-repository attached, detached, and alternate-directory checkouts, plus repeated-pack validation and an LF line-ending checkout contract.
- Repository-pinned local tool installation and restore instructions, with installed `capture`, `check`, and `diff` validation on Windows, Linux, and macOS.
