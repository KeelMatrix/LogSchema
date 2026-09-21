# Phase 0 feasibility gate

Status: Corrected prototype evidence for the bounded source-generated
`[LoggerMessage]` declaration model described below; independent re-verification
is still required. This milestone contains no shipping CLI, comparison engine,
package project, or telemetry integration.

## Verdict rule

The specification's section 30 says:

> Pass: Canonical manifests are stable across repeated machines/build directories, every supported declaration is extracted correctly, and no target application code needs to execute.

It also says:

> Fail: Extraction requires brittle generated-source scraping, silently omits supported declarations, or requires executing arbitrary target application code. If the gate fails, do not implement the product as specified; surface the contradiction for founder decision.

The corrected probe satisfies the extraction conditions in the fixture matrix:
it loads projects semantically through MSBuild/Roslyn, extracts project-source
declarations even when their document names end in `.g.cs` or their merged
symbols carry `GeneratedCodeAttribute`, reports unsupported declarations
explicitly, proves compiler-generated replicas against one project-source
declaration each, and produces byte-identical manifests across the final
Windows and Linux runs.

## Semantic loading boundary

The probe uses these APIs:

- `MSBuildLocator.RegisterDefaults()` to select the installed SDK MSBuild.
- `MSBuildWorkspace.Create(...)` and `OpenProjectAsync(...)` to evaluate an
  SDK-style project.
- `Project.GetCompilationAsync()` to obtain the Roslyn compilation.
- `Document.GetSemanticModelAsync()`, `SemanticModel.GetDeclaredSymbol(...)`,
  and `IMethodSymbol.GetAttributes()` to read declarations and compile-time
  attribute values.
- `Project.Documents` is the provenance boundary for project source. Every
  declaration found there is treated as a source declaration regardless of its
  filename or merged symbol attributes; this includes the synthetic
  `Generated.Logging.g.cs` file marked as output from `Other.Generator`.
- Compilation syntax trees that are not project documents are inspected only
  for true source-generator output. A generated implementation is excluded
  only when its stable method identity has exactly one project-source
  declaration. That proof is recorded in `generatedImplementationReplicas`
  through `pairedSource`; an unpaired generated declaration is extracted or
  reported in `unsupported`.

The probe source contains no `Assembly.Load`, `Process.Start`, target-assembly
invocation, or generated-assembly invocation. Each fixture has a reachable
`[ModuleInitializer]` that calls `ExecutionSentinel.ThrowIfExecuted()` and
would throw if its assembly loaded. The probe only calls
`MSBuildWorkspace.OpenProjectAsync` and `Project.GetCompilationAsync` with
`DesignTimeBuild=true` and `BuildingProject=false`; those operations read the
project, syntax trees, symbols, and attributes and do not load or invoke the
fixture assembly. The regression script also checks this source-level proof;
it never executes the sentinel.

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
| `EventName` constant expression | Extracted | `LoggingMatrix.cs:35`, `ConstantExpressionEventName` | `EventId=1008`, `EventName=ConstantExpression` |
| `SkipEnabledCheck` | Extracted | `LoggingMatrix.cs:38`, `SkipEnabledCheck` | `EventId=1009`, declaration retained with the named generator option |
| All attribute arguments named | Extracted | `LoggingMatrix.cs:41`, `AllNamedArguments` | `EventId=1010`, `EventName=AllNamedArguments` |
| Duplicate `EventId` values | Extracted | `LoggingMatrix.cs:44` and `:47`, `DuplicateEventIdOne`/`Two` | Both declarations retained with `EventId=1011` |
| Nested type and partial outer type | Extracted | `LoggingMatrix.cs:71`, `NestedEvent` | Containing type is `PartialOuter.NestedLogging` |
| Non-static containing class | Extracted | `LoggingMatrix.cs:78`, `InstanceEvent` | Instance declaration retained with `EventId=1015` |
| Generic logging method | Not extracted; reported | `LoggingMatrix.cs:50`, `GenericEvent` | Explicitly reported as unsupported because the bounded probe excludes generic methods |
| Method with no parameters | Not extracted; reported | `LoggingMatrix.cs:53`, `NoParameters` | Explicitly reported as unsupported because no `ILogger` parameter identifies the logger |
| `.g.cs` from a different generator | Extracted | `Generated.Logging.g.cs:7`, `GeneratedPartial` | Project-document provenance wins over the filename and merged `GeneratedCodeAttribute`; `EventId=1101` |
| Non-partial method with explicit `Message`/`EventId` | Not extracted; reported | `LoggingMatrix.cs:56`, `NonPartialWithExplicitValues` | Explicitly reported as unsupported because the logging generator requires a partial method |

Fixture results:

| Fixture | Target framework | Extracted | Unsupported | Compilation/workspace diagnostic kinds |
| --- | --- | ---: | ---: | --- |
| `Phase0.Net8` | `net8.0` | 15 | 3 | `CS8795:Error` / none |
| `Phase0.Stable` | `net10.0` (`10.0.401`) | 15 | 3 | `CS8795:Error` / none |
| `Phase0.Multi` | `net8.0` | 15 | 3 | `CS8795:Error` / none |
| `Phase0.Multi` | `net10.0` | 15 | 3 | `CS8795:Error` / none |

The same 18 source occurrences were classified on both multi-target builds, so
both target frameworks are supported by this probe. The compiler diagnostic is
the expected partial-method diagnostic for deliberately unsupported generic and
no-logger shapes; it does not cause either declaration to disappear.

## Unsupported and generated declarations

The unsupported user declarations are the generic method, the no-parameter
method, and the non-partial method. Their full declarations and reasons are
present in the manifest's `unsupported` array; none is silently omitted.

The raw fixture count is 54 `[LoggerMessage]` occurrences: 18 in each of the
three fixture projects (15 extracted and 3 unsupported). The manifest also
contains 45 compiler-generated `LoggerMessage.g.cs` replicas (15 for each
fixture). Those replicas are not additional source occurrences: each has
`source.kind=generated`, canonical line `0`, and a `pairedSource` pointing to
one distinct extracted project declaration. Therefore the durable accounting is:

| Scope | Raw source occurrences | Extracted | Unsupported | Paired generated replicas |
| --- | ---: | ---: | ---: | ---: |
| Each fixture project | 18 | 15 | 3 | 15 |
| Three fixture projects | 54 | 45 | 9 | 45 |

The 45 replicas are the compiler implementations for the 45 extracted
declarations; they are excluded from the source-occurrence total. In
particular, `GeneratedPartial` appears once as the project-source declaration
at `Generated.Logging.g.cs:7` and once as its proven `LoggerMessage.g.cs`
implementation replica, rather than being counted as two source declarations.
Generated line numbers are canonicalized to `0` because generator layout is
not a source contract and varies between hosts.

## Determinism evidence

The final Windows determinism run used two distinct output directories for each
fixture. The exact commands were:

```text
dotnet phase0/bin/Release/net10.0/Phase0.LogSchemaProbe.dll fixtures/Phase0.Net8/Phase0.Net8.csproj --output artifacts/determinism-a/net8.json --tfm net8.0
dotnet phase0/bin/Release/net10.0/Phase0.LogSchemaProbe.dll fixtures/Phase0.Net8/Phase0.Net8.csproj --output artifacts/determinism-b/net8.json --tfm net8.0
dotnet phase0/bin/Release/net10.0/Phase0.LogSchemaProbe.dll fixtures/Phase0.Stable/Phase0.Stable.csproj --output artifacts/determinism-a/stable.json --tfm net10.0
dotnet phase0/bin/Release/net10.0/Phase0.LogSchemaProbe.dll fixtures/Phase0.Stable/Phase0.Stable.csproj --output artifacts/determinism-b/stable.json --tfm net10.0
dotnet phase0/bin/Release/net10.0/Phase0.LogSchemaProbe.dll fixtures/Phase0.Multi/Phase0.Multi.csproj --output artifacts/determinism-a/multi-net8.json --tfm net8.0
dotnet phase0/bin/Release/net10.0/Phase0.LogSchemaProbe.dll fixtures/Phase0.Multi/Phase0.Multi.csproj --output artifacts/determinism-b/multi-net8.json --tfm net8.0
dotnet phase0/bin/Release/net10.0/Phase0.LogSchemaProbe.dll fixtures/Phase0.Multi/Phase0.Multi.csproj --output artifacts/determinism-a/multi-net10.json --tfm net10.0
dotnet phase0/bin/Release/net10.0/Phase0.LogSchemaProbe.dll fixtures/Phase0.Multi/Phase0.Multi.csproj --output artifacts/determinism-b/multi-net10.json --tfm net10.0
```

The corresponding SHA-256 values and individual durations were:

```text
determinism-a net8: C43D0CCC7E09C98947144EA398A2A3028648BFE1CAFE55100381B897B02BE6A5 (9.40s)
determinism-b net8: C43D0CCC7E09C98947144EA398A2A3028648BFE1CAFE55100381B897B02BE6A5 (7.03s)
determinism-a stable: 9E7BC1780B92E1FCE958592E98104137E0197AFAFB4DDFFF97C9B4C6DF63BF7B (7.10s)
determinism-b stable: 9E7BC1780B92E1FCE958592E98104137E0197AFAFB4DDFFF97C9B4C6DF63BF7B (5.78s)
determinism-a multi-net8: 4CB781446F0327AC43FF6ED2458D1198E153DCA6AC95E38826A48B3697139CA6 (6.12s)
determinism-b multi-net8: 4CB781446F0327AC43FF6ED2458D1198E153DCA6AC95E38826A48B3697139CA6 (6.45s)
determinism-a multi-net10: 5829F353FEB5837F2B215E4113A4CF34A2D757C2D712B40B83A32B62D616B6B4 (5.44s)
determinism-b multi-net10: 5829F353FEB5837F2B215E4113A4CF34A2D757C2D712B40B83A32B62D616B6B4 (5.87s)
deterministic=PASS
```

All four hashes changed from the previously reviewed candidate because the canonical
manifest now includes the previously omitted `GeneratedPartial`, the expanded
shape matrix, and explicit replica-pair provenance. The prior reviewed values
were net8 `92B40CA8…3215`, stable `F87CAFB3…19FB`, multi net8
`CF7D7FFB…B9E6`, and multi net10 `092D59BF…FBC9`; the changes are expected,
not nondeterminism.

## Linux evidence

Docker was available with client/server `29.7.2`. The final run used
`mcr.microsoft.com/dotnet/sdk:10.0.401`; the container reported SDK `10.0.401`
on Ubuntu 24.04, restored the probe and net8 fixture, built the probe with
zero warnings and errors, and extracted 15 declarations with 3 unsupported
declarations. The same net8 fixture produced the following matching hash in
22.44 seconds:

```text
Windows SHA-256: C43D0CCC7E09C98947144EA398A2A3028648BFE1CAFE55100381B897B02BE6A5
Linux SHA-256:   C43D0CCC7E09C98947144EA398A2A3028648BFE1CAFE55100381B897B02BE6A5
cross-platform-determinism=PASS
```

## Reproduction commands

Run the local CI-equivalent matrix from the repository root:

```text
pwsh ./build/validate.ps1
```

The final `validate.ps1` run returned exit code `0`. Its observed wall-clock
duration was approximately `61` seconds (the four probe output timestamps ran
from 3:01:12 PM through 3:01:45 PM on the Windows host; the run also included
restore and build). The printed step timings began:

```text
Restore probe: 3.31s
Restore net8 fixture: 3.82s
Restore stable fixture: 2.24s
Restore multi-target fixture: 2.65s
Build probe: 4.31s
Probe net8: 9.89s
Probe stable SDK fixture: approximately 14s
Probe multi-target net8: approximately 9s
Probe multi-target net10: approximately 10s
validate exit code: 0
validate wall-clock: approximately 61s
```

The hashes printed by that one final validation run were:

```text
Phase0.Net8 net8.0: C43D0CCC7E09C98947144EA398A2A3028648BFE1CAFE55100381B897B02BE6A5
Phase0.Stable net10.0: 9E7BC1780B92E1FCE958592E98104137E0197AFAFB4DDFFF97C9B4C6DF63BF7B
Phase0.Multi net8.0: 4CB781446F0327AC43FF6ED2458D1198E153DCA6AC95E38826A48B3697139CA6
Phase0.Multi net10.0: 5829F353FEB5837F2B215E4113A4CF34A2D757C2D712B40B83A32B62D616B6B4
```

`validate.ps1` already failed closed on a nonzero native command by checking
`$LASTEXITCODE`; the fix now captures that code immediately after each native
action before reporting elapsed time, so a later PowerShell operation cannot
mask the build or probe failure.

The final Linux command used the pinned image and equivalent operations:

```text
docker create --name logschema-phase0-linux-fix-20260921 -w /workspace mcr.microsoft.com/dotnet/sdk:10.0.401 bash -lc "set -eu; dotnet --version; dotnet restore phase0/Phase0.LogSchemaProbe.csproj --configfile NuGet.config; dotnet restore fixtures/Phase0.Net8/Phase0.Net8.csproj --configfile NuGet.config; dotnet build phase0/Phase0.LogSchemaProbe.csproj -c Release --no-restore --nologo; dotnet phase0/bin/Release/net10.0/Phase0.LogSchemaProbe.dll fixtures/Phase0.Net8/Phase0.Net8.csproj --output artifacts/linux-net8.json --tfm net8.0; sha256sum artifacts/linux-net8.json"
docker cp phase0 logschema-phase0-linux-fix-20260921:/workspace/phase0
docker cp fixtures logschema-phase0-linux-fix-20260921:/workspace/fixtures
docker cp Directory.Build.props logschema-phase0-linux-fix-20260921:/workspace/Directory.Build.props
docker cp Directory.Packages.props logschema-phase0-linux-fix-20260921:/workspace/Directory.Packages.props
docker cp global.json logschema-phase0-linux-fix-20260921:/workspace/global.json
docker cp NuGet.config logschema-phase0-linux-fix-20260921:/workspace/NuGet.config
docker start -a logschema-phase0-linux-fix-20260921
```

The container emitted SDK `10.0.401`, a zero-warning/zero-error probe build,
and the matching SHA-256 shown above. The disposable final container was
removed after evidence capture.

## Icon path derivation

M1 intentionally has no packable project and therefore produces no package or
package icon copy. The exact path set required when the shipping tool project
is added is one file only:

```text
<repository root>/icon.png
```

The intended pack configuration for a future `src/KeelMatrix.LogSchema`
packable project is:

```xml
<PackageIcon>icon.png</PackageIcon>
<None Include="..\..\icon.png" Pack="true" PackagePath="" Link="icon.png" />
```

That item resolves to the repository-root file and embeds it as package-root
`icon.png`; no project-local icon path is required. The root `icon.png` is
absent in M1 as expected. Its bytes, placement, commit, and push remain
founder-owned and are not performed by repository tooling.
