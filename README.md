# KeelMatrix.LogSchema

**LogSchema fails CI when a source-generated .NET logging contract changes incompatibly.** Capture `[LoggerMessage]` event identities and structured fields into a deterministic baseline, then review additions, removals, renames, level changes, and template changes before deployment.

LogSchema is a local .NET tool. It reads SDK-style C# projects through design-time MSBuild/Roslyn APIs and never loads or invokes the target application assembly.

## Install

The recommended installation is a repository-pinned local tool manifest:

```bash
dotnet new tool-manifest
dotnet tool install KeelMatrix.LogSchema
dotnet tool run logschema capture MyService.csproj --output logschema.json
dotnet tool run logschema check MyService.csproj --baseline logschema.json
```

Commit `dotnet-tools.json` and `logschema.json` after reviewing them. On a fresh checkout, restore the manifest-pinned version before running LogSchema:

```bash
dotnet tool restore
dotnet tool run logschema check MyService.csproj --baseline logschema.json
```

### Prerequisites

The tool requires the .NET 8 runtime. Project loading requires a full .NET SDK/MSBuild installation; v1 project-loading behavior is verified with .NET SDK `10.0.401`, the version pinned by `global.json`. Restore the target project with that SDK before capture or check. The verified fixtures target `net8.0` and `net10.0`; support is not claimed for project systems that the pinned SDK and design-time MSBuild workspace cannot evaluate.

### Global tool alternative

For a disposable trial, install the tool globally and use the bare command. This path is separate from the manifest-pinned workflow:

```bash
dotnet tool install --global KeelMatrix.LogSchema
logschema capture MyService.csproj --output logschema.json
logschema check MyService.csproj --baseline logschema.json
```

## Quick start

After an intentional or accidental change:

```bash
dotnet tool run logschema check MyService.csproj --baseline logschema.json
```

Compare two manifests without loading a project:

```bash
dotnet tool run logschema diff old-logschema.json new-logschema.json
```

## What is a log contract?

A log contract is the stable, reviewable shape of a supported source-generated logging event: its project identity, containing type, method identity, EventId, EventName, level source, message template, message-template placeholder occurrences, generator-effective parameter roles, and emitted structured-state properties in method-parameter order. A placeholder is a template occurrence; structured state is the unique set of emitted properties. The manifest may contain operational vocabulary such as event names and property names, so review it like source code.

## Supported declaration scope

V1 supports C# methods using `Microsoft.Extensions.Logging.LoggerMessageAttribute` when the declaration has compile-time attribute values, is a partial void method, is inside a partial type, and has a generator-supported parameter model. The first applicable `ILogger`, exception-derived, and dynamic `LogLevel` parameters are special; later candidates remain ordinary structured-state parameters. Every other ordinary parameter is structured state even when absent from the message template. A matched placeholder controls the emitted property's casing, while repeated occurrences do not duplicate one state property. Constants, named arguments, explicit and default EventName values, nested/partial types, Unicode text, exception/logger/LogLevel parameters, escaped placeholders, and format specifiers are represented. A placeholder for the first logger or dynamic-level parameter is explicitly unsupported; a placeholder for the first exception is represented with the generator's additional exception/state behavior. Unsupported discovered declarations are listed in the manifest with a reason; they are never silently omitted.

V1 does not inspect runtime logs, manual/interpolated logging calls, providers, secrets, PII, hosted registries, or target application execution.

## Compatibility rules

The complete rule matrix and stable diagnostic codes are maintained in [COMPATIBILITY-RULES.md](COMPATIBILITY-RULES.md). In short:

| Change | Default classification | Default CI gate |
| --- | --- | --- |
| Event added | INFO | no |
| Event removed or method identity changed | BREAKING | yes |
| EventId or EventName changed | BREAKING | yes |
| Structured-state property removed or renamed | BREAKING | yes |
| Structured-state property order changed | BREAKING | yes |
| Structured-state property added | INFO | no |
| Generator-effective parameter role changed | BREAKING | yes |
| LogLevel changed | WARNING | no; use `--severity warning` |
| Message template changed with the same structured state | INFO | no |

Structured identity fields are compared with exact ordinal semantics. The reader parses every declared type with Roslyn and rejects text that is not byte-identical to the canonical rendering: namespace-qualified, without `global::`, `@`-escaped identifiers, trivia/comments, or non-canonical generic spacing. The manifest's type `form` is a structural identity projection; the persisted parameter `role` is the source-derived generator model. Both the embedded form and redundant `parameterForms` position must match the reader's structural type classification, including rejection of wrong-arity or generic-rooted nested logger members, while parameter names, first-special roles, and structured-state membership are validated as a coherent model. Derived exception events remain supported even though their declared type has form `None`. Case-only changes to emitted structured-property names are structured renames (`KMLOG102`), while `KMLOG301` is reserved for message-template changes whose effective structured state is unchanged under ordinal comparison.

Intentional changes can be accepted explicitly with repeated `--accept <diagnostic-code>`. Acceptance affects only the current comparison; it never rewrites a baseline.

## Capture, check, and update workflow

`capture` writes the canonical `logschema.json` (or the path supplied by `--output`). `check` analyzes the current project and compares it with `--baseline`; it never rewrites that file. To update an intentional baseline, run `capture` explicitly, review the diff, and commit the result. `diff` compares two existing manifests offline.

Common options are `--format text|json`, `--severity breaking|warning|all`, `--accept <code>`, `--no-telemetry`, and `--tfm <target-framework>`. The telemetry option is reserved and accepted for script portability. V1 contains no telemetry client and makes no LogSchema-owned telemetry network requests.

## CI and exit codes

```bash
dotnet tool run logschema check MyService.csproj --baseline logschema.json --format json
```

Exit codes are:

| Code | Meaning |
| ---: | --- |
| 0 | Analysis succeeded and no finding at or above the selected gate exists. |
| 1 | Analysis succeeded and a gated finding exists. |
| 2 | Invocation or configuration is invalid. |
| 3 | Project loading, manifest parsing, or analysis failed, including zero supported events. |

A project-load failure is always code 3 and cannot produce a clean result. JSON output separates `toolErrors`, `analysisErrors`, and compatibility `findings`:

```json
{
  "toolErrors": [],
  "analysisErrors": [],
  "findings": [],
  "manifestPath": null,
  "eventCount": 84,
  "unsupportedCount": 0,
  "unsupported": [],
  "coverageComplete": true
}
```

## Manifest and determinism

Manifest schema version 1 is documented in [MANIFEST.md](MANIFEST.md). It is UTF-8 without a BOM, uses invariant canonical values, sorts every list with ordinal ordering, and contains no absolute machine paths. Source provenance uses non-lossy logical namespaces: `project/<relative-path>` for project documents, `external/up-N/<relative-tail>` for outside-project documents, and `generated/<generator>/<hint>` or a content hash for generated trees. Dot-prefixed names are preserved, and provenance collisions fail closed as `KMLOGP008`. Parsing is bounded to 4 MiB and depth 32 and rejects malformed input, a future schema version, non-canonical declared type text, and event identities that contradict their containing type, method, generic arity, parameter count, ref kinds, or reader-recomputed parameter forms. V1 does not assert arbitrary type-hierarchy semantics in the manifest. Every read-time or comparison-time analysis error returns exit 3 with no findings and `coverageComplete: false`.

## Diagnostics

The stable diagnostic reference is in [COMPATIBILITY-RULES.md](COMPATIBILITY-RULES.md). Text and JSON expose the same finding fields: code, severity, project key, logical identity, event name, affected field, old/new values, and message. Normal diagnostics do not print absolute machine paths.

## Cross-platform and SDK support

The public CI workflow validates the built package, local-manifest installation, and installed `capture`, `check`, and `diff` commands on Windows, Linux, and macOS with SDK `10.0.401`. The tool targets `net8.0`, has no OS-specific command syntax, and uses centrally pinned Roslyn/MSBuild dependencies. After dependencies are restored, LogSchema itself makes no product-owned network requests during `capture`, `check`, or `diff`; `capture` and `check` still invoke MSBuild evaluation, so target projects and their imports or tasks may have their own network behavior.

## Privacy and security

No source, manifest, path, project identity, or schema content is uploaded. V1 has no telemetry client. LogSchema does not execute target application code and does not read runtime values. Roslyn/MSBuild project evaluation still has the normal trust boundary of the local machine; do not run it on untrusted projects without appropriate isolation. Manifest input is treated as untrusted and is bounded and fail-closed.

Project analysis also fails closed with exit code 3 when an input exceeds 64 C# projects, 512 documents in one project, or 4,096 documents across the input. These are safety ceilings rather than performance promises; the [project-analysis resource gate](docs/PROJECT-ANALYSIS-RESOURCES.md) records the reproducible fixture and measured validation budget.

See the [privacy statement](PRIVACY.md), [security policy](SECURITY.md), and [dependency rationale](docs/DEPENDENCIES.md) for the repository's data and supply-chain boundaries.

## Troubleshooting

- **Project-load failure:** confirm the SDK is installed, restore the project with its normal package sources, and pass the intended target framework with `--tfm` for a multi-targeted project. The command returns 3 on failure.
- **Zero events (`KMLOGP006`):** `capture` and `check` return 3 when no supported `[LoggerMessage]` declarations are discovered, and `capture` writes no baseline. Check that the project genuinely contains declarations, that their shape is supported, and that the intended target framework is selected with `--tfm`. Manual `ILogger.Log*` calls are outside v1. A zero-event baseline is also rejected by `check`; `diff` is unaffected.
- **Unsupported declaration:** inspect the manifest `unsupported` list and its reason. The declaration was reported rather than discarded.
- **Invalid manifest:** regenerate it with `capture`. Malformed JSON, contradictory or non-canonical event identities, unknown parameter vocabularies, absolute source paths, oversized/deep input, and future schema versions are rejected with code 3.
- **Duplicate or ambiguous identity:** correct duplicate partial declarations, project identities, or source-provenance identities. The manifest records an analysis error and comparison cannot report clean. `KMLOGP008` means a source path could not be represented safely or distinct syntax trees would share one logical provenance.

Microsoft's generator diagnostics validate declaration correctness at compile time. LogSchema adds a persisted, deterministic history comparison. It is also distinct from logging governance/style analyzers and from secret/redaction tooling: it checks compatibility of declared event shapes only.

## Development

```text
dotnet restore LogSchema.slnx --configfile NuGet.config
dotnet build LogSchema.slnx -c Release --no-restore
dotnet test tests/KeelMatrix.LogSchema.Tests/KeelMatrix.LogSchema.Tests.csproj -c Release --no-restore
pwsh ./build/validate.ps1
```

The Phase 0 semantic probe remains in `phase0/`. The same committed semantic fixtures are exercised separately against the shipping extractor by `build/Test-ShippingMatrix.ps1`; see [semantic fixture validation](docs/PHASE0-FEASIBILITY.md). The shipping tool is in `src/KeelMatrix.LogSchema/`, and its project-local README is packed at the NuGet package root.

Repository text sources use canonical LF line endings so a normal Windows clone with `core.autocrlf=true` remains format-clean. `dotnet pack` uses a fixed deterministic ZIP timestamp and emits portable PDBs with canonical GitHub SourceLink mappings for the exact repository commit. `build/validate.ps1` inspects both package archives, their portable PDBs, and their SourceLink records, rejects private machine paths and unexpected content, and requires artifact bytes to remain identical across attached, detached, and alternate-directory checkouts that use the canonical repository URL.

`build/Test-ReleaseVersion.ps1` is the source of truth for the pre-tag changelog, release-date, and package-version contract. The tag-triggered release workflow reruns that check before building or publishing. Its manual `workflow_dispatch` path is dry-run only: it exercises the release-equivalent restore, build, test, vulnerability, pack, archive, SourceLink, and installed-tool gates, uploads the validated artifacts for inspection, and cannot authenticate, publish, or create a GitHub Release.

## License

MIT. See [LICENSE](LICENSE).
