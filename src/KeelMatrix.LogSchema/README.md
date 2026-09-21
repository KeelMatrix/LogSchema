# KeelMatrix.LogSchema

**LogSchema fails CI when a source-generated .NET logging contract changes incompatibly.** Capture `[LoggerMessage]` event identities and structured fields into a deterministic baseline, then review additions, removals, renames, level changes, and template changes before deployment.

The recommended installation is a repository-pinned local tool manifest:

```bash
dotnet new tool-manifest
dotnet tool install KeelMatrix.LogSchema
logschema capture MyService.csproj --output logschema.json
```

For a one-off experiment, use `dotnet tool install --global KeelMatrix.LogSchema` immediately after the local-manifest path above. Then run `logschema check MyService.csproj --baseline logschema.json` in CI. `logschema diff old.json new.json` compares manifests offline.

The log contract is the EventId, EventName, level, template, and ordered structured placeholder shape of a supported partial void method using `LoggerMessageAttribute`. V1 reads C# projects through design-time MSBuild/Roslyn, does not execute target code, and reports unsupported declarations explicitly.

See the repository [compatibility rules](https://github.com/KeelMatrix/LogSchema/blob/main/COMPATIBILITY-RULES.md) and [manifest schema](https://github.com/KeelMatrix/LogSchema/blob/main/MANIFEST.md) for the complete stable-code and schema contracts.

Options: `--format text|json`, `--severity breaking|warning|all`, `--accept <code>`, `--no-telemetry`, and `--tfm <target-framework>`. Exit codes are 0 clean, 1 gated findings, 2 invalid invocation/configuration, and 3 project-load or analysis failure. V1 emits no telemetry; `--no-telemetry` is reserved and accepted.
