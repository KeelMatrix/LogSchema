# Changelog

Changes for the KeelMatrix.LogSchema package are recorded here using the Keep a Changelog format.

## [Unreleased]

### Changed

- Added public CI validation for Windows, Linux, and macOS using the SDK pinned in `global.json`; [CI run 35874369927](https://github.com/KeelMatrix/LogSchema/actions/runs/35874369927) verified all three legs.

### Added

- Deterministic schema-v1 manifests for supported source-generated `LoggerMessage` declarations.
- Offline `capture`, `check`, and `diff` commands with text and JSON diagnostics and CI-friendly exit codes.
- Stable compatibility classification for event, identity, structured-placeholder, level, and template changes.
- Explicit unsupported-declaration and analysis-error reporting without target application execution.
- A net8.0 .NET tool package with the `logschema` command, package-local README, and package validation that proves artifact bytes are independent of checkout attachment, clone origin, and clone directory.
- An isolated package-consumer smoke path covering installation, help, capture, clean checks, breaking diagnostics, and invalid configuration.
- Exact ordinal structured-identity comparison, including case-only placeholder renames as breaking `KMLOG102` findings.
- Fail-closed `KMLOGP006` handling for zero supported events, with no baseline written by `capture` and zero-event baselines rejected by `check`.
- Byte-deterministic `.nupkg` and `.snupkg` output plus repeated-pack validation and an LF line-ending checkout contract.
