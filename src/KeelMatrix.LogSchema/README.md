# KeelMatrix.LogSchema

**LogSchema fails CI when a source-generated .NET logging contract changes incompatibly.** Capture `[LoggerMessage]` event identities and structured fields into a deterministic baseline, then review additions, removals, renames, level changes, and template changes before deployment.

## Install

The recommended installation is a repository-pinned local tool manifest:

```bash
dotnet new tool-manifest
dotnet tool install KeelMatrix.LogSchema
logschema capture MyService.csproj --output logschema.json
```

For just trying it, use `dotnet tool install --global KeelMatrix.LogSchema` immediately below the local-manifest path. Then run `logschema check MyService.csproj --baseline logschema.json` in CI. `logschema diff old.json new.json` compares manifests offline.

## Quick start

```bash
logschema capture MyService.csproj --output logschema.json
logschema check MyService.csproj --baseline logschema.json
logschema diff old.json new.json
```

The log contract is the EventId, EventName, level, template, and ordered structured placeholder shape of a supported partial void method using `LoggerMessageAttribute`. V1 reads C# projects through design-time MSBuild/Roslyn, does not execute target code, and reports unsupported declarations explicitly.

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

Structured identity fields use exact ordinal, case-sensitive comparison. A case-only placeholder rename is `KMLOG102`; `KMLOG301` is only a true prose-only change with unchanged structured shape. If no supported `[LoggerMessage]` declarations are found, `KMLOGP006` is an analysis error: `capture` and `check` return 3, `capture` writes no baseline, and `check` rejects a zero-event baseline. `diff` remains a pure manifest comparison.

See the repository [compatibility rules](https://github.com/KeelMatrix/LogSchema/blob/main/COMPATIBILITY-RULES.md) and [manifest schema](https://github.com/KeelMatrix/LogSchema/blob/main/MANIFEST.md) for the complete stable-code and schema contracts.

Options: `--format text|json`, `--severity breaking|warning|all`, `--accept <code>`, `--no-telemetry`, and `--tfm <target-framework>`. Exit codes are 0 clean, 1 gated findings, 2 invalid invocation/configuration, and 3 project-load or analysis failure. V1 emits no telemetry; `--no-telemetry` is reserved and accepted.

The tool reads SDK-style C# projects through design-time MSBuild/Roslyn APIs. It does not execute target application code, inspect runtime logs, or replace secret/redaction tooling. After restore, analysis is local and does not require a hosted service.

For troubleshooting and the full supported declaration scope, see the [repository README](https://github.com/KeelMatrix/LogSchema/blob/main/README.md), [compatibility rules](https://github.com/KeelMatrix/LogSchema/blob/main/COMPATIBILITY-RULES.md), and [manifest schema](https://github.com/KeelMatrix/LogSchema/blob/main/MANIFEST.md).

## License

MIT
