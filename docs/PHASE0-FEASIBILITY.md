# Phase 0 feasibility gate

Status: PASS for the bounded source-generated `[LoggerMessage]` declaration
model described below. This milestone contains no shipping CLI, comparison
engine, package project, or telemetry integration.

## Verdict rule

The specification's section 30 says:

> Pass: Canonical manifests are stable across repeated machines/build directories, every supported declaration is extracted correctly, and no target application code needs to execute.

It also says:

> Fail: Extraction requires brittle generated-source scraping, silently omits supported declarations, or requires executing arbitrary target application code. If the gate fails, do not implement the product as specified; surface the contradiction for founder decision.

The probe passes the stated rule: it loads projects semantically through
MSBuild/Roslyn, extracts the supported declarations in the fixture matrix,
reports unsupported and compiler-generated replica declarations explicitly,
and produces byte-identical manifests across the final Windows and Linux runs.

## Semantic loading boundary

The probe uses these APIs:

- `MSBuildLocator.RegisterDefaults()` to select the installed SDK MSBuild.
- `MSBuildWorkspace.Create(...)` and `OpenProjectAsync(...)` to evaluate an
  SDK-style project.
- `Project.GetCompilationAsync()` to obtain the Roslyn compilation.
- `Document.GetSemanticModelAsync()`, `SemanticModel.GetDeclaredSymbol(...)`,
  and `IMethodSymbol.GetAttributes()` to read declarations and compile-time
  attribute values.
- Compilation syntax trees are inspected only to report source-generator
  implementation replicas. Those replicas are recorded in
  `generatedImplementationReplicas` and are not counted as additional source
  declarations.

The probe source contains no `Assembly.Load`, `Process.Start`, target-assembly
invocation, or generated-assembly invocation. The fixtures contain static
execution sentinels that would throw if target code ran; the probe only reads
syntax trees, symbols, and attributes. MSBuild evaluation is a project-loading
operation, not execution of the target application's entry point or types.

## SDK and compatibility matrix

The repository pins SDK `10.0.401` in `global.json`. On 2026-09-21, the host
reported `10.0.401`, and Microsoft's current .NET 10 download page listed
SDK `10.0.401` as the latest .NET 10 SDK and .NET 10 as the active LTS channel:

- <https://dotnet.microsoft.com/download/dotnet/10.0>
- <https://github.com/dotnet/core/blob/main/release-notes/releases-index.json>

The current stable SDK fixture is therefore `net10.0`. The multi-target
fixture uses `net8.0;net10.0`, the stable equivalent of the specification's
`net8.0;net9.0` example. Both target frameworks were loaded and extracted
successfully.

Each row below is extracted from the source declaration shown, not from
generated-source text scraping. Results were the same for the net8 fixture,
the net10 fixture, and each target of the multi-target fixture.

| Case | Result | Source declaration | Canonical result |
| --- | --- | --- | --- |
| Constants and named arguments | Extracted | `LoggingMatrix.cs:14`, `ConstantArguments` | `EventId=1001`, `EventName=OrderCreated`, `Level=Warning`, `Order {OrderId} for {CustomerName}`, placeholders `OrderId, CustomerName` |
| Constructor attribute arguments | Extracted | `LoggingMatrix.cs:17`, `ConstructorArguments` | `EventId=1002`, default `EventName=ConstructorArguments`, `Level=Error`, exception plus `ILogger`, placeholder `OrderId` |
| Default `EventName` | Extracted | `LoggingMatrix.cs:20`, `DefaultEventName` | `EventName=DefaultEventName` when the attribute omits `EventName` |
| Explicit `EventName` | Extracted | `LoggingMatrix.cs:23`, `ExplicitEventName` | `EventId=1004`, `EventName=ExplicitEventName`, `Level=Debug`, placeholder `RequestId` |
| `ILogger` and `LogLevel` parameter forms | Extracted | `LoggingMatrix.cs:26`, `LoggerAndLevel` | `EventId=1005`, `Level=Information`, special forms recorded as `ILogger, LogLevel` |
| Unicode event/template text | Extracted | `LoggingMatrix.cs:29`, `UnicodeTemplate` | `EventId=1006`, `EventName=UnicodeTemplate`, `Level=Critical`, Unicode template and placeholder preserved |
| Escaped braces and format specifier | Extracted | `LoggingMatrix.cs:32`, `FormatSpecifier` | `EventId=1007`, `Level=Trace`, `Escaped {{literal}} and {Value:000}`, placeholder token retained |
| Generated-file partial declaration | Extracted | `Generated.Logging.g.cs:7`, `GeneratedPartial` | `EventId=1101`, default `EventName=GeneratedPartial`, `Level=Information`, placeholder `Value` |
| Non-partial method with explicit `Message`/`EventId` | Not extracted; reported | `LoggingMatrix.cs:35`, `NonPartialWithExplicitValues` | Explicitly reported as unsupported because the logging generator requires a partial method |

Fixture results:

| Fixture | Target framework | Extracted | Unsupported | Compilation/workspace diagnostic kinds |
| --- | --- | ---: | ---: | --- |
| `Phase0.Net8` | `net8.0` | 7 | 1 | none |
| `Phase0.Stable` | `net10.0` (`10.0.401`) | 7 | 1 | none |
| `Phase0.Multi` | `net8.0` | 7 | 1 | none |
| `Phase0.Multi` | `net10.0` | 7 | 1 | none |

The same source declarations were extracted on both multi-target builds, so
both target frameworks are supported by this probe.

## Unsupported and generated declarations

The only unsupported user declaration is the non-partial method documented in
the matrix. Its full declaration and reason are present in the manifest's
`unsupported` array; it is not silently omitted.

The compiler-generated `LoggerMessage.g.cs` replicas are also reported in the
manifest's `generatedImplementationReplicas` array. There are nine replicas
for this fixture: `GeneratedPartial` appears twice (the generated-file fixture
and the compiler-generated implementation), and each of the seven supported
source declarations has one compiler-generated implementation replica. Replica
declarations are recorded verbatim and excluded from the event set so the
source declaration is counted once. Generated line numbers are deliberately
canonicalized to `0`, because generator layout is not a source contract and
varied between hosts during the determinism check.

## Determinism evidence

The final Windows run used two distinct output directories for the same net8
source and produced:

```text
output-a-seconds=5.45
output-b-seconds=5.03
output-a-sha256=5DB01A3C17C171A0F776B87B7E31750E00D8E0E72F69594A7B8B8E2D0D6B12ED
output-b-sha256=5DB01A3C17C171A0F776B87B7E31750E00D8E0E72F69594A7B8B8E2D0D6B12ED
deterministic=PASS
```

The repository validation matrix also produced these Windows hashes:

```text
Phase0.Net8 net8.0: 92B40CA8D0790773F33DAF7067EA5C13B8D8CED26E2177A3C48E19B1A20A3215
Phase0.Stable net10.0: F87CAFB3180A32A5467122A9FD42FB900614BAD89F27557F8C1D549E041319FB
Phase0.Multi net8.0: CF7D7FFB70C730729C11D2252C9196F0D3BCC6CC8B17664E8BECA7363E1CB9E6
Phase0.Multi net10.0: 092D59BF96C9AE8C3FC9909B4F12A77FF0DF9CF827431514CBF256A16D83FBC9
```

## Linux evidence

Docker was available with client/server `29.7.2`. The final run used
`mcr.microsoft.com/dotnet/sdk:10.0.401`; the container reported SDK `10.0.401`
on Ubuntu 24.04, restored the probe and net8 fixture, built the probe with
zero warnings and errors, and extracted 7 declarations with 1 unsupported
declaration. The same net8 fixture produced:

```text
Windows SHA-256: 92B40CA8D0790773F33DAF7067EA5C13B8D8CED26E2177A3C48E19B1A20A3215
Linux SHA-256:   92B40CA8D0790773F33DAF7067EA5C13B8D8CED26E2177A3C48E19B1A20A3215
cross-platform-determinism=PASS
```

## Reproduction commands

Run the local CI-equivalent matrix from the repository root:

```text
pwsh ./build/validate.ps1
```

The final run durations were:

```text
Restore probe: 1.30s
Restore net8 fixture: 1.08s
Restore stable fixture: 1.26s
Restore multi-target fixture: 1.48s
Build probe: 2.17s
Probe net8: 6.52s
Probe stable SDK fixture: 5.99s
Probe multi-target net8: 6.02s
Probe multi-target net10: 6.94s
```

The final Linux command used the pinned image and equivalent operations:

```text
docker create --name logschema-phase0-linux-7 -w /workspace mcr.microsoft.com/dotnet/sdk:10.0.401 bash -lc "set -eu; dotnet --version; dotnet restore phase0/Phase0.LogSchemaProbe.csproj --configfile NuGet.config; dotnet restore fixtures/Phase0.Net8/Phase0.Net8.csproj --configfile NuGet.config; dotnet build phase0/Phase0.LogSchemaProbe.csproj -c Release --no-restore --nologo; dotnet phase0/bin/Release/net10.0/Phase0.LogSchemaProbe.dll fixtures/Phase0.Net8/Phase0.Net8.csproj --output artifacts/linux-net8.json --tfm net8.0; sha256sum artifacts/linux-net8.json"
docker cp phase0 logschema-phase0-linux-7:/workspace/phase0
docker cp fixtures logschema-phase0-linux-7:/workspace/fixtures
docker cp Directory.Build.props logschema-phase0-linux-7:/workspace/Directory.Build.props
docker cp Directory.Packages.props logschema-phase0-linux-7:/workspace/Directory.Packages.props
docker cp global.json logschema-phase0-linux-7:/workspace/global.json
docker cp NuGet.config logschema-phase0-linux-7:/workspace/NuGet.config
docker start -a logschema-phase0-linux-7
```

The container emitted SDK `10.0.401`, a zero-warning/zero-error probe build,
and the matching SHA-256 shown above. The disposable final container was
removed after evidence capture.

