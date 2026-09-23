# Dependency rationale

This document explains the dependency boundary for the v1 `KeelMatrix.LogSchema` tool. The Release package gate reports the exact generated nuspec dependencies and the complete restored Core graph; the commands below reproduce that evidence.

The shipping tool has one runtime dependency graph for semantic project loading and canonical comparison:

- `Microsoft.CodeAnalysis.CSharp.Workspaces` and `Microsoft.CodeAnalysis.Workspaces.MSBuild` provide semantic C# documents and design-time SDK project loading. They are required to discover `LoggerMessageAttribute` declarations without executing target assemblies.
- `Microsoft.Build.Locator` selects the installed MSBuild instance used by the workspace. It is a project-loading dependency, not a runtime logging provider.
- `Microsoft.Build.Framework`, `Microsoft.Build`, `Microsoft.Build.Utilities.Core`, `Microsoft.Build.Tasks.Core`, and `Microsoft.NET.StringTools` are private build/runtime support assets required by the MSBuild workspace and are excluded from consumer compile/runtime asset flow where the locator requires it.
- `Microsoft.Extensions.Logging.Abstractions` supplies the current logging attribute and symbol definitions used by representative projects; LogSchema does not depend on a logging provider or backend.
- `System.Formats.Asn1` is pinned at `9.0.1` in the Core restore graph as a deliberate security-floor dependency and is checked by the package validation gate.
- `System.Text.Json` is supplied by the net8.0 framework and is used for the bounded schema-v1 serializer/parser.

The CLI parser is framework code so the public tool does not add a large parser dependency. `Microsoft.NET.Test.Sdk`, `xunit`, and `xunit.runner.visualstudio` are test-only dependencies. No telemetry client, network service, provider SDK, or hosted backend is included.

Versions are centrally pinned in `Directory.Packages.props`. The Phase 0 probe keeps its validated Roslyn 5.9 override; the shipping net8.0 path uses the net8-compatible Roslyn 4.14 line.

## Verification

Run the following after controlled restore to print direct and transitive package versions and produce the same versioned machine-readable vulnerability reports consumed by the repository gate:

```powershell
dotnet package list --project src/KeelMatrix.LogSchema.Core/KeelMatrix.LogSchema.Core.csproj --include-transitive --framework net8.0 --no-restore
dotnet package list --project src/KeelMatrix.LogSchema/KeelMatrix.LogSchema.csproj --include-transitive --framework net8.0 --no-restore
dotnet package list --project src/KeelMatrix.LogSchema.Core/KeelMatrix.LogSchema.Core.csproj --vulnerable --include-transitive --framework net8.0 --no-restore --config NuGet.config --format json --output-version 1
dotnet package list --project src/KeelMatrix.LogSchema/KeelMatrix.LogSchema.csproj --vulnerable --include-transitive --framework net8.0 --no-restore --config NuGet.config --format json --output-version 1
```

`build/Test-VulnerabilityReport.ps1` validates the report version, requested direct-and-transitive audit mode, controlled vulnerability source, expected project identity, and every reported advisory. It fails on any finding, nonzero audit command result, malformed or incomplete report, missing source, or unexpected project, so failure to obtain usable audit data cannot be reported as a clean result. Regression cases cover a clean report, a vulnerability finding, command failure, and unusable audit data.

Every resolved package serves semantic project loading, the supported CLI, or framework-provided serialization. The package gate separately inspects both generated archives and nuspecs, confirms the consumer-visible dependency set and exact expected contents, and rejects vulnerable or unrelated content.
