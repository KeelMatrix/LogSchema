# Repository guide

## Navigation

- `phase0/` contains the non-packable MSBuild/Roslyn feasibility probe.
- `fixtures/` contains SDK-style projects used by the Phase 0 matrix.
- `docs/` contains the Phase 0 evidence and decision record.
- `LogSchema.slnx` is the repository solution.

## Commands

```text
dotnet restore LogSchema.slnx
dotnet build LogSchema.slnx -c Release --no-restore
pwsh ./build/validate.ps1
```

The Phase 0 probe is run directly with a fixture project and an output path. The
probe is intentionally non-packable and must never become part of the shipping
tool package.

## Invariants

- The Phase 0 probe loads SDK-style projects through MSBuild/Roslyn semantic APIs.
- The probe never loads or invokes target application assemblies.
- Canonical output contains no absolute checkout, build, or temporary paths.
- Unsupported `[LoggerMessage]` declarations are reported explicitly.
- The repository-root `icon.png` is founder-owned and must not be created or edited
  by repository tooling.
- This M1 repository contains no shipping CLI, manifest comparison engine, or
  package project.

## Scope discipline

Keep fixtures synthetic and bounded. Do not add runtime log inspection, provider
integrations, generic logging analysis, telemetry, or release automation in M1.

