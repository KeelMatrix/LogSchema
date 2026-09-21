# KeelMatrix.LogSchema

**LogSchema fails CI when a source-generated .NET logging contract changes incompatibly.** Capture `[LoggerMessage]` event identities and structured fields into a deterministic baseline, then review additions, removals, renames, level changes, and template changes before deployment.

LogSchema is a local .NET tool. It reads SDK-style C# projects through design-time MSBuild/Roslyn APIs and never loads or invokes the target application assembly.

## Install and first use

The recommended installation is a repository-pinned local tool manifest:

```bash
dotnet new tool-manifest
dotnet tool install KeelMatrix.LogSchema
logschema capture MyService.csproj --output logschema.json
```

Commit the manifest and `logschema.json` after reviewing the contract. For a quick one-off experiment, the alternative is:

```bash
dotnet tool install --global KeelMatrix.LogSchema
```

After an intentional or accidental change:

```bash
logschema check MyService.csproj --baseline logschema.json
```

Compare two manifests without loading a project:

```bash
logschema diff old-logschema.json new-logschema.json
```

## What is a log contract?

A log contract is the stable, reviewable shape of a supported source-generated logging event: its project identity, containing type, method identity, EventId, EventName, level, message template, and structured placeholder names in order. The manifest may contain operational vocabulary such as event names and property names, so review it like source code.

## Supported declaration scope

V1 supports C# methods using `Microsoft.Extensions.Logging.LoggerMessageAttribute` when the declaration has compile-time attribute values, is a partial void method, is inside a partial type, has an `ILogger` parameter, and uses message placeholders that match ordinary method parameters. Constants, named arguments, explicit and default EventName values, nested/partial types, Unicode text, exception/logger/LogLevel parameters, escaped placeholders, and format specifiers are represented. Unsupported discovered declarations are listed in the manifest with a reason; they are never silently omitted.

V1 does not inspect runtime logs, manual/interpolated logging calls, providers, secrets, PII, hosted registries, or target application execution.

## Compatibility rules

The complete rule matrix and stable diagnostic codes are maintained in [COMPATIBILITY-RULES.md](COMPATIBILITY-RULES.md). In short:

| Change | Default classification | Default CI gate |
| --- | --- | --- |
| Event added | INFO | no |
| Event removed or method identity changed | BREAKING | yes |
| EventId or EventName changed | BREAKING | yes |
| Structured placeholder removed or renamed | BREAKING | yes |
| Structured placeholder order changed | BREAKING | yes |
| Structured placeholder added | INFO | no |
| LogLevel changed | WARNING | no; use `--severity warning` |
| Template prose changed with the same structured shape | INFO | no |

Intentional changes can be accepted explicitly with repeated `--accept <diagnostic-code>`. Acceptance affects only the current comparison; it never rewrites a baseline.

## Capture, check, and update workflow

`capture` writes the canonical `logschema.json` (or the path supplied by `--output`). `check` analyzes the current project and compares it with `--baseline`; it never rewrites that file. To update an intentional baseline, run `capture` explicitly, review the diff, and commit the result. `diff` compares two existing manifests offline.

Common options are `--format text|json`, `--severity breaking|warning|all`, `--accept <code>`, and `--no-telemetry`. The telemetry option is reserved and accepted for script portability; v1 emits no telemetry because the bounded company integration is intentionally omitted.

## CI and exit codes

```bash
logschema check MyService.csproj --baseline logschema.json --format json
```

Exit codes are:

| Code | Meaning |
| ---: | --- |
| 0 | Analysis succeeded and no finding at or above the selected gate exists. |
| 1 | Analysis succeeded and a gated finding exists. |
| 2 | Invocation or configuration is invalid. |
| 3 | Project loading, manifest parsing, or analysis failed. |

A project-load failure is always code 3 and cannot produce a clean result. JSON output separates `toolErrors`, `analysisErrors`, and compatibility `findings`:

```json
{
  "toolErrors": [],
  "analysisErrors": [],
  "findings": [],
  "manifestPath": null,
  "eventCount": 84,
  "unsupportedCount": 0
}
```

## Manifest and determinism

Manifest schema version 1 is documented in [MANIFEST.md](MANIFEST.md). It is UTF-8 without a BOM, uses invariant canonical values, sorts every list with ordinal ordering, normalizes paths to project-relative `/` separators, and contains no absolute machine paths. Parsing is bounded to 4 MiB and depth 32 and rejects malformed input or a future schema version.

## Diagnostics

The stable diagnostic reference is in [COMPATIBILITY-RULES.md](COMPATIBILITY-RULES.md). Text and JSON expose the same finding fields: code, severity, project key, logical identity, event name, affected field, old/new values, and message. Normal diagnostics do not print absolute machine paths.

## Cross-platform and SDK support

The tool targets `net8.0` and is designed for Windows, Linux, and macOS SDK-style C# projects. The repository development SDK is pinned to `10.0.401`; the project loader uses centrally pinned Roslyn/MSBuild dependencies. Cross-platform evidence is recorded by the validation command and must be checked before making a release claim. The tool does not require a network connection after restore.

## Privacy and security

No source, manifest, path, project identity, or schema content is uploaded. V1 has no telemetry. LogSchema does not execute target application code and does not read runtime values. Roslyn/MSBuild project evaluation still has the normal trust boundary of the local machine; do not run it on untrusted projects without appropriate isolation. Manifest input is treated as untrusted and is bounded and fail-closed.

## Troubleshooting

- **Project-load failure:** confirm the SDK is installed, restore the project with its normal package sources, and pass the intended target framework with `--tfm` for a multi-targeted project. The command returns 3 on failure.
- **Zero events:** confirm the project contains supported partial `[LoggerMessage]` declarations. Manual `ILogger.Log*` calls are outside v1.
- **Unsupported declaration:** inspect the manifest `unsupported` list and its reason. The declaration was reported rather than discarded.
- **Invalid manifest:** regenerate it with `capture`. Malformed JSON, absolute source paths, oversized/deep input, and future schema versions are rejected with code 3.
- **Duplicate or ambiguous identity:** correct duplicate partial declarations or project identities. The manifest records an analysis error and comparison cannot report clean.

Microsoft's generator diagnostics validate declaration correctness at compile time. LogSchema adds a persisted, deterministic history comparison. It is also distinct from logging governance/style analyzers and from secret/redaction tooling: it checks compatibility of declared event shapes only.

## Development

```text
dotnet restore LogSchema.slnx --configfile NuGet.config
dotnet build LogSchema.slnx -c Release --no-restore
dotnet test tests/KeelMatrix.LogSchema.Tests/KeelMatrix.LogSchema.Tests.csproj -c Release --no-restore
pwsh ./build/validate.ps1
```

The Phase 0 semantic probe and fixtures remain in `phase0/` and `fixtures/` as regression evidence. The shipping tool is in `src/KeelMatrix.LogSchema/`; its project-local README is the README input for the later package step.
