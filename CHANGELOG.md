# Changelog

Changes for the KeelMatrix.LogSchema package are recorded here using the Keep a Changelog format.

## [Unreleased]

### Added

- Deterministic schema-v1 manifests for supported source-generated `LoggerMessage` declarations.
- Offline `capture`, `check`, and `diff` commands with text and JSON diagnostics and CI-friendly exit codes.
- Stable compatibility classification for event, identity, structured-placeholder, level, and template changes.
- Explicit unsupported-declaration and analysis-error reporting without target application execution.
- A net8.0 .NET tool package with the `logschema` command, package-local README, and deterministic package validation.
- An isolated package-consumer smoke path covering installation, help, capture, clean checks, breaking diagnostics, and invalid configuration.
