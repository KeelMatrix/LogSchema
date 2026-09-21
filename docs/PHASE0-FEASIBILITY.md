# Phase 0 feasibility gate

Status: bounded prototype evidence updated for the declaration-pairing,
identity, and execution-boundary findings. Independent re-verification remains
required. This milestone contains no shipping CLI, comparison engine, package
project, or telemetry integration.

Implementation candidate SHA for this evidence set:
`aec7790b356628cba0199ec92c28654e6b8084f2`

## Verdict rule and evidence standard

Specification section 30 defines the gate:

> Pass: Canonical manifests are stable across repeated machines/build directories, every supported declaration is extracted correctly, and no target application code needs to execute.

> Fail: Extraction requires brittle generated-source scraping, silently omits supported declarations, or requires executing arbitrary target application code.

This document records evidence against that rule. It does not claim a formal
proof of absence. The evidence standard is:

- the probe uses design-time-only MSBuild evaluation with
  `DesignTimeBuild=true` and `BuildingProject=false`;
- the probe has no target-assembly load, invocation, or process-start path;
- each probe fixture has a reachable `[ModuleInitializer]` sentinel that would
  throw if the fixture assembly were loaded and initialized;
- a separate controlled child-process test deliberately loads and initializes
  a sentinel-only fixture and observes `Target application code executed.`.

The controlled load test validates the sentinel itself. It does not change the
probe or use the sentinel as an extraction mechanism.

## Semantic loading boundary

The probe uses `MSBuildLocator.RegisterDefaults`,
`MSBuildWorkspace.Create`, `OpenProjectAsync`,
`Project.GetCompilationAsync`, document semantic models, declared symbols, and
compile-time attribute values. Project documents are the provenance boundary:
their declarations remain source declarations even when a filename ends in
`.g.cs` or a merged symbol has `GeneratedCodeAttribute`.

Compilation syntax trees that are not project documents are inspected only for
generated output. A generated implementation is paired only when its stable
method identity has exactly one project-source declaration. Ambiguous or
unpaired cases are retained in `unsupported` and/or `analysisIssues`; they are
not removed by count-based deduplication.

The source audit in `build/Test-Phase0Matrix.ps1` checks that `phase0/Program.cs`
contains no `Assembly.Load`, `Assembly.LoadFrom`, `Process.Start`,
`GetEntryAssembly`, or target-assembly invocation path. It also checks the
design-time workspace options and the reachable module-initializer sentinels.

## Pairing-boundary fixture coverage

`fixtures/Phase0.Pairing` is a committed synthetic fixture. Its assertions in
`build/Test-Phase0Matrix.ps1` cover:

| Case | Result |
| --- | --- |
| Same stable method identity in `Separate/First.cs` and `Separate/Second.cs` | Both source declarations retained; `KMLOGP001` error emitted; generated pairing is not guessed. |
| Same stable method identity twice in `SameDocument.cs` | Both source declarations retained; `KMLOGP001` error emitted. |
| Source-generator-produced `[LoggerMessage]` declaration in `generated/Unpaired.LoggerMessage.g.cs` with no project-source counterpart | Explicitly reported in `unsupported`; `KMLOGP002` warning emitted. |
| Built-in generated implementations for ambiguous identities | Explicitly reported as unpaired/ambiguous instead of being silently discarded. |

The fixture also includes ref-kind overloads and generic-arity variants. The
source generator is in `fixtures/Phase0.GeneratedDeclarationGenerator`; its
Debug output is built by the narrow matrix test before the MSBuild/Roslyn
probe loads the pairing project.

## Identity and source-location rule

`GetDeclarationKey` now uses this stable shape:

```text
<fully-qualified containing type>.<method>`<generic arity>(<ref-kind>:<type>,...)
```

Every parameter includes its `RefKind`, and the method's generic arity is
included even for arity zero. Extracted events additionally record
`genericArity` and `parameterRefKinds`. Regression assertions distinguish the
`None` and `Ref` overloads and distinguish generic arities 1 and 2.

Recorded project-source locations are normalized project-relative paths with
forward slashes. The nested pairing fixture proves this with
`Separate/First.cs` and `Separate/Second.cs`. Generated locations use the
canonical virtual project-relative prefix `generated/` so build-directory
paths cannot enter a manifest.

## Windows matrix evidence

The final narrow matrix command was:

```text
pwsh -NoProfile -File ./build/Test-Phase0Matrix.ps1
```

It passed in 25.60 seconds. The command output included these durations:

```text
Build pairing generator: 0.96s
Probe net8: 4.40s
Probe stable: 4.48s
Probe multi-net8: 4.38s
Probe multi-net10: 4.53s
Probe pairing: 4.77s
Build sentinel fixture: 1.02s
Controlled sentinel load: 0.34s
Phase 0 matrix regression test passed.
```

The four Windows manifests from the final validation run are:

| Fixture / target | SHA-256 |
| --- | --- |
| `Phase0.Net8` / `net8.0` | `0CF8E71D3C93A9DDFD1E9334AF5F1F509262B8ABB1A72673EDFE18C49A718B85` |
| `Phase0.Stable` / `net10.0` | `481C129059A595F51500E7755F1895196E501159D21804E55AA22586679DB23B` |
| `Phase0.Multi` / `net8.0` | `724A0BEDD512C16CB0D9D68B77F6CDB8E9FCF78A83A323681CB490D6BD74D29A` |
| `Phase0.Multi` / `net10.0` | `93EAA480B7C199B30F563DFE0F502575F002E99508990B01A1711444113B9EF8` |

The same four hashes were stable across two distinct final Windows output
directories. Each ordinary fixture reports 15 extracted declarations
and 3 explicit unsupported declarations.

The repeated-output commands used the same probe invocation with output paths
under `artifacts/determinism-final-a` and `artifacts/determinism-final-b`:

```text
dotnet phase0/bin/Release/net10.0/Phase0.LogSchemaProbe.dll fixtures/Phase0.Net8/Phase0.Net8.csproj --output artifacts/determinism-final-{a|b}/net8.json --tfm net8.0
dotnet phase0/bin/Release/net10.0/Phase0.LogSchemaProbe.dll fixtures/Phase0.Stable/Phase0.Stable.csproj --output artifacts/determinism-final-{a|b}/stable.json --tfm net10.0
dotnet phase0/bin/Release/net10.0/Phase0.LogSchemaProbe.dll fixtures/Phase0.Multi/Phase0.Multi.csproj --output artifacts/determinism-final-{a|b}/multi-net8.json --tfm net8.0
dotnet phase0/bin/Release/net10.0/Phase0.LogSchemaProbe.dll fixtures/Phase0.Multi/Phase0.Multi.csproj --output artifacts/determinism-final-{a|b}/multi-net10.json --tfm net10.0
```

The eight individual command durations were `a/net8 6.66s`, `a/stable
6.43s`, `a/multi-net8 6.60s`, `a/multi-net10 5.17s`, `b/net8 5.99s`,
`b/stable 6.76s`, `b/multi-net8 7.04s`, and `b/multi-net10 6.64s`; all four
comparisons reported `MATCH`.

## Linux evidence

Linux parity is **unverified on this host**. No Linux hash is claimed.

The required file-backed attempt used image
`mcr.microsoft.com/dotnet/sdk:10.0.401`. The attempt created a disposable
container, copied the repository checkout into a workspace, and attempted to
restore and build the probe, run it against the net8 fixture, and record the
resulting SHA-256. Starting the container produced no output through the
command bridge, and the container remained in `Created` state before it was
removed. A separate minimal container-start test produced the same no-output
observation. These are command-bridge/runtime observations, not probe results;
Linux remains unverified on this host.

A host-independent re-proof requires another host or CI runner with a
functioning Docker daemon: create the pinned image container, copy the exact
checkout, run the file-writing script to completion, copy out
`artifacts/linux-net8.json` and `linux.sha256`, and compare its SHA-256 with
the Windows net8 value
`0CF8E71D3C93A9DDFD1E9334AF5F1F509262B8ABB1A72673EDFE18C49A718B85`.

## Full validation evidence

The final validation command was:

```text
pwsh -NoProfile -File ./build/validate.ps1
```

It returned exit code 0 in 30.13 seconds. Step timings were:

```text
Restore probe: 1.58s
Restore net8 fixture: 1.29s
Restore stable fixture: 1.30s
Restore multi-target fixture: 1.38s
Restore pairing fixture: 1.13s
Restore sentinel fixture: 1.05s
Build probe: 1.95s
Probe net8: 4.99s
Probe stable SDK fixture: 5.33s
Probe multi-target net8: 4.99s
Probe multi-target net10: 4.65s
```

No remote CI workflow was added or used. The repository remains private and
contains no packable project, so the required icon path set remains empty for
this Phase 0 prototype.
