# KeelMatrix.LogSchema

**LogSchema fails CI when a source-generated .NET logging contract changes incompatibly.** Capture `[LoggerMessage]` event identities and structured fields into a deterministic baseline, then review additions, removals, renames, level changes, and template changes before deployment.

## Install

The recommended installation is a repository-pinned local tool manifest:

```bash
dotnet new tool-manifest
dotnet tool install KeelMatrix.LogSchema
dotnet tool run logschema capture MyService.csproj --output logschema.json
dotnet tool run logschema check MyService.csproj --baseline logschema.json
```

Commit `dotnet-tools.json` and the reviewed baseline. On a fresh checkout, restore the manifest-pinned version before running LogSchema:

```bash
dotnet tool restore
dotnet tool run logschema check MyService.csproj --baseline logschema.json
```

The tool requires the .NET 8 runtime. Project loading requires a full .NET SDK/MSBuild installation; v1 is verified with SDK `10.0.401`, including `net8.0` and `net10.0` fixtures. Restore the target project with that SDK before analysis.

### Global tool alternative

For a disposable trial, keep the global installation path separate:

```bash
dotnet tool install --global KeelMatrix.LogSchema
logschema capture MyService.csproj --output logschema.json
logschema check MyService.csproj --baseline logschema.json
```

## Quick start

```bash
dotnet tool run logschema capture MyService.csproj --output logschema.json
dotnet tool run logschema check MyService.csproj --baseline logschema.json
dotnet tool run logschema diff old.json new.json
```

The log contract is the effective EventId, EventName, level source, message template, template placeholder occurrences, generator-effective parameter roles, and emitted structured-state properties of a supported partial void method using `LoggerMessageAttribute`. A placeholder is one message-template occurrence; structured state is the unique emitted property list in method-parameter order. An omitted EventId follows the pinned generator's deterministic default, an omitted EventName follows the method name, and a level supplied by the first applicable `LogLevel` parameter is recorded as `Dynamic` rather than `None`. Source diagnostics use deterministic logical provenance (`project/`, `external/up-N/`, or `generated/` identities), preserve dot-prefixed names, and fail closed on unsafe or colliding identities. V1 reads C# projects through design-time MSBuild/Roslyn, does not execute target code, and reports unsupported declarations explicitly.

## Compatibility summary

The complete matrix is in the linked [compatibility rules](https://github.com/KeelMatrix/LogSchema/blob/main/COMPATIBILITY-RULES.md). The v1 default `breaking` gate treats these changes as follows:

| Change | Severity | Default gate |
| --- | --- | --- |
| Event removal or method identity change | BREAKING | yes |
| EventId/EventName change | BREAKING | yes |
| Structured-state property removal, rename, or order change | BREAKING | yes |
| Event or structured-state property addition | INFO | no |
| Generator-effective parameter-role change | BREAKING | yes |
| LogLevel change | WARNING | no; use `--severity warning` |
| Message-template change with unchanged structured state | INFO | no |

Structured identity fields use exact ordinal, case-sensitive comparison. A case-only emitted-property rename is `KMLOG102`; `KMLOG301` is only a message-template change with unchanged effective structured state. The first applicable `ILogger`, exception-derived, and dynamic `LogLevel` parameters are special; later candidates and fixed-level `LogLevel` parameters are structured state. Ordinary parameters remain structured state even when not mentioned in the template, and repeated placeholder occurrences do not duplicate state properties. If no supported `[LoggerMessage]` declarations are found, `KMLOGP006` is an analysis error: `capture` and `check` return 3, `capture` writes no baseline, and `check` rejects a zero-event baseline. Unsupported declarations are shown with identities and reasons; `check` and `diff` return 3 with `KMLOGP007` because their supported-subset comparison is incomplete. `diff` remains a pure comparison for the zero-event rule, but it still rejects incomplete unsupported coverage.

The reader parses every declared type with Roslyn and rejects text that is not byte-identical to the canonical rendering: namespace-qualified, without `global::`, `@`-escaped identifiers, trivia/comments, or non-canonical generic spacing. The identity `form` is classified structurally: `ILogger` only for top-level `Microsoft.Extensions.Logging.ILogger` with arity 0 or exactly one type argument, `LogLevel` only for exact top-level `Microsoft.Extensions.Logging.LogLevel`, `Exception` only for exact top-level `System.Exception`, and `None` for every other top-level canonical type, including wrong-arity or generic-rooted nested logger members and derived exceptions. Raw type prefixes and suffixes are never trusted. Both the embedded form and redundant `parameterForms` position must match, while persisted source-derived roles and structured-state entries are validated for coherent names, order, and first-special semantics. Derived exception events remain supported and use form `None`; source analysis still preserves their effective exception role. Read-time and comparison-time analysis errors return exit code 3 with no findings and `coverageComplete: false`.

See the repository [compatibility rules](https://github.com/KeelMatrix/LogSchema/blob/main/COMPATIBILITY-RULES.md) and [manifest schema](https://github.com/KeelMatrix/LogSchema/blob/main/MANIFEST.md) for the complete stable-code and schema contracts.

Options: `--format text|json`, `--severity breaking|warning|all`, `--accept <code>`, `--no-telemetry`, and `--tfm <target-framework>`. Exit codes are 0 clean, 1 gated findings, 2 invalid invocation/configuration, and 3 project-load or analysis failure. V1 contains no telemetry client; `--no-telemetry` is reserved and accepted.

The tool reads SDK-style C# projects through design-time MSBuild/Roslyn APIs. It does not execute target application code, inspect runtime logs, or replace secret/redaction tooling. After dependencies are available, LogSchema itself makes no product-owned network requests and does not require a hosted service; `capture` and `check` still invoke MSBuild evaluation, so target projects and their imports or tasks may have their own network behavior.

Project analysis fails closed with exit code 3 when an input exceeds 64 C# projects, 512 documents in one project, or 4,096 documents across the input. These are safety ceilings rather than performance promises; see the repository's [project-analysis resource gate](https://github.com/KeelMatrix/LogSchema/blob/main/docs/PROJECT-ANALYSIS-RESOURCES.md) for the reproducible fixture and validation budget.

## Platform support

The tool is supported on Windows, Linux, and macOS. The public CI matrix installs the built package through a local tool manifest and runs `capture`, `check`, and `diff` on all three platforms with SDK `10.0.401`.

For troubleshooting and the full supported declaration scope, see the [repository README](https://github.com/KeelMatrix/LogSchema/blob/main/README.md), [compatibility rules](https://github.com/KeelMatrix/LogSchema/blob/main/COMPATIBILITY-RULES.md), [manifest schema](https://github.com/KeelMatrix/LogSchema/blob/main/MANIFEST.md), and [privacy statement](https://github.com/KeelMatrix/LogSchema/blob/main/PRIVACY.md).

## License

MIT
