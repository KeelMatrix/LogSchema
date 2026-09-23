# Repository guide

## Navigation

- `src/KeelMatrix.LogSchema/` is the packable net8.0 command-line tool.
- `src/KeelMatrix.LogSchema.Core/` is the non-packable extractor, manifest model/parser, and comparison engine.
- `tests/KeelMatrix.LogSchema.Tests/` contains unit and contract tests.
- `phase0/` and `fixtures/` retain the non-shipping semantic feasibility probe and regression fixtures.
- `COMPATIBILITY-RULES.md` and `MANIFEST.md` are the source of truth for durable product contracts.

## Commands

```text
dotnet restore LogSchema.slnx --configfile NuGet.config
dotnet build LogSchema.slnx -c Release --no-restore
dotnet test tests/KeelMatrix.LogSchema.Tests/KeelMatrix.LogSchema.Tests.csproj -c Release --no-restore
pwsh ./build/validate.ps1
```

The tool uses design-time MSBuild/Roslyn evaluation and never loads target application assemblies. The Phase 0 probe is intentionally retained and must continue to pass its matrix.

## Invariants

- Shipping output is a deterministic schema-v1 manifest with no absolute machine paths.
- Unsupported declarations are explicit; duplicate or ambiguous identities are analysis errors.
- `check` never rewrites a baseline.
- The root `icon.png` must not be created or edited by repository tooling.
- Tests, fixtures, and the Phase 0 probe are non-packable.
