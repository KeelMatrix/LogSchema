# Manifest schema v1

The default file is `logschema.json`. The top-level `schemaVersion` is an integer and must be `1`; the package version is independent. A future schema version is rejected rather than reinterpreted.

## Shape

- `projects`: project identity records with a stable key, project name, assembly name, and selected target framework.
- `events`: supported event records with project key, full method identity, containing type, method, generic arity, parameter ref kinds, EventId, EventName, level, message template, ordered placeholders, special parameter forms, and project-relative source provenance.
- `unsupported`: every discovered but unsupported declaration, including normalized declaration text, identity, source provenance, and reason.
- `analysisIssues`: explicit warnings/errors such as ambiguous identity, unpaired generated declarations, workspace failures, or an untrustworthy compilation.
- `compilationDiagnosticKinds` and `workspaceDiagnosticKinds`: sorted diagnostic categories without source paths or raw machine-specific messages.

## Canonicalization

Manifests are UTF-8 without a BOM, use invariant culture for numeric and enum values, and end with one LF. Every list is sorted with ordinal comparison: projects by key; events by project key then identity; unsupported declarations by project key, source file, line, and declaration key; analysis issues by project key, code, and declaration key; diagnostic-kind lists ordinally. Project source paths are relative to the project directory, use `/`, and generated paths use `generated/<file-name>`. Absolute paths, drive letters, and backslashes are not emitted.

The serializer normalizes line endings in declaration text and JSON output. Source content is never read into telemetry or used to derive an absolute path. The same source and selected target framework therefore produce byte-identical output regardless of checkout/output directory, line-ending checkout, or host path separator.

## Parsing safety and compatibility

The parser accepts at most 4 MiB and JSON nesting depth 32. It rejects malformed JSON, comments, trailing commas, incomplete required fields, non-canonical source paths, oversized record arrays, and any schema version other than 1. A failed parse is an analysis failure and returns exit code 3. Baselines are never changed by `check` or `diff`.

## Trust boundary

Capture uses design-time MSBuild/Roslyn semantic loading. It does not use `Assembly.Load`, invoke target methods, or inspect runtime logging. MSBuild project evaluation remains a local-machine trust boundary; project restore and SDK installation occur outside normal offline analysis.
