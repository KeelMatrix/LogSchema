# KeelMatrix.LogSchema

This repository contains the Phase 0 feasibility scaffold for LogSchema.

The product goal is a local .NET tool that captures and compares source-generated
`[LoggerMessage]` contracts. Full v1 implementation is intentionally not part of
this repository milestone. The current repository contains only the semantic
project-loading probe, its synthetic matrix fixtures, and the feasibility record.

## Development

The repository is pinned to .NET SDK `10.0.401`. Restore and build the probe with:

```text
dotnet restore LogSchema.slnx
dotnet build LogSchema.slnx -c Release --no-restore
```

Run the local validation path with:

```text
pwsh ./build/validate.ps1
```

See [docs/PHASE0-FEASIBILITY.md](docs/PHASE0-FEASIBILITY.md) for the matrix,
semantic loading boundary, deterministic-output evidence, and the current gate
verdict.

