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

The log contract is the effective EventId, EventName, level, template, and ordered structured placeholder shape of a supported partial void method using `LoggerMessageAttribute`. An omitted EventId follows the pinned generator's deterministic default, an omitted EventName follows the method name, and a level supplied by a `LogLevel` parameter is recorded as `Dynamic` rather than `None`. V1 reads C# projects through design-time MSBuild/Roslyn, does not execute target code, and reports unsupported declarations explicitly.

## Compatibility summary

The complete matrix is in the linked [compatibility rules](https://github.com/KeelMatrix/LogSchema/blob/main/COMPATIBILITY-RULES.md). The v1 default `breaking` gate treats these changes as follows:

| Change | Severity | Default gate |
| --- | --- | --- |
| Event removal or method identity change | BREAKING | yes |
| EventId/EventName change | BREAKING | yes |
| Structured placeholder removal, rename, or order change | BREAKING | yes |
| Event or structured placeholder addition | INFO | no |
| LogLevel change | WARNING | no; use `--severity warning` |
| Template prose change with unchanged structured shape | INFO | no |

Structured identity fields use exact ordinal, case-sensitive comparison. A case-only placeholder rename is `KMLOG102`; `KMLOG301` is only a true prose-only change with unchanged structured shape. If no supported `[LoggerMessage]` declarations are found, `KMLOGP006` is an analysis error: `capture` and `check` return 3, `capture` writes no baseline, and `check` rejects a zero-event baseline. Unsupported declarations are shown with identities and reasons; `check` and `diff` return 3 with `KMLOGP007` because their supported-subset comparison is incomplete. `diff` remains a pure comparison for the zero-event rule, but it still rejects incomplete unsupported coverage.

The reader parses every declared type with Roslyn and rejects text that is not byte-identical to the canonical rendering: namespace-qualified, without `global::`, `@`-escaped identifiers, trivia/comments, or non-canonical generic spacing. It then classifies structurally: `ILogger` only for top-level `Microsoft.Extensions.Logging.ILogger` with arity 0 or exactly one type argument, `LogLevel` only for exact top-level `Microsoft.Extensions.Logging.LogLevel`, `Exception` only for exact top-level `System.Exception`, and `None` for every other top-level canonical type, including wrong-arity or generic-rooted nested logger members and derived exceptions. Raw type prefixes and suffixes are never trusted. Both the embedded form and redundant `parameterForms` position must match, so coordinated, one-sided, and additive forgeries fail closed. Derived exception events remain supported and use form `None`; capture uses semantic inheritance only to keep exception parameters out of message placeholders. V1 does not assert arbitrary type-hierarchy semantics in the manifest. Read-time and comparison-time analysis errors return exit code 3 with no findings and `coverageComplete: false`.

See the repository [compatibility rules](https://github.com/KeelMatrix/LogSchema/blob/main/COMPATIBILITY-RULES.md) and [manifest schema](https://github.com/KeelMatrix/LogSchema/blob/main/MANIFEST.md) for the complete stable-code and schema contracts.

Options: `--format text|json`, `--severity breaking|warning|all`, `--accept <code>`, `--no-telemetry`, and `--tfm <target-framework>`. Exit codes are 0 clean, 1 gated findings, 2 invalid invocation/configuration, and 3 project-load or analysis failure. V1 contains no telemetry client; `--no-telemetry` is reserved and accepted.

The tool reads SDK-style C# projects through design-time MSBuild/Roslyn APIs. It does not execute target application code, inspect runtime logs, or replace secret/redaction tooling. After restore, analysis is local and does not require a hosted service.

## Platform support

The tool is supported on Windows, Linux, and macOS. The public CI matrix installs the built package through a local tool manifest and runs `capture`, `check`, and `diff` on all three platforms with SDK `10.0.401`.

For troubleshooting and the full supported declaration scope, see the [repository README](https://github.com/KeelMatrix/LogSchema/blob/main/README.md), [compatibility rules](https://github.com/KeelMatrix/LogSchema/blob/main/COMPATIBILITY-RULES.md), [manifest schema](https://github.com/KeelMatrix/LogSchema/blob/main/MANIFEST.md), and [privacy statement](https://github.com/KeelMatrix/LogSchema/blob/main/PRIVACY.md).

## License

MIT
