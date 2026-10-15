# Changelog

Changes for the KeelMatrix.LogSchema package are recorded here using the Keep a Changelog format.

## [Unreleased]

## [0.1.0] - 2026-10-16

### Added

- A local .NET tool that captures deterministic schema-v1 baselines from supported source-generated `[LoggerMessage]` declarations, checks projects against a baseline, and compares manifests offline.
- Compatibility diagnostics for changes to event identities, IDs, names, levels, message templates, and emitted structured properties, with configurable severity and explicit acceptance of intentional findings.
- Fail-closed reporting for unsupported declarations and analysis errors; project analysis does not load or invoke target application assemblies.
