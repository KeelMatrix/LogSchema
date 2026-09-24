# Semantic fixture and project-loading validation

This document describes the committed fixtures used to validate LogSchema's design-time project-loading boundary. It distinguishes the historical feasibility probe from the shipping extractor so either implementation can be reproduced without treating one as evidence for the other.

## Implementations

- `phase0/` is a non-packable feasibility probe. It exercises direct MSBuild/Roslyn extraction and remains useful for detecting changes to the underlying project-loading assumptions.
- `src/KeelMatrix.LogSchema.Core/` is the shipping extractor used by the packaged `logschema` tool. `build/Test-ShippingMatrix.ps1` runs the semantic fixture matrix against this implementation.
- `build/validate.ps1` runs both matrices and then installs the built NuGet tool through a local tool manifest for consumer-level `capture`, `check`, and `diff` tests.

Success of the feasibility probe alone is not evidence that the shipping implementation behaves correctly.

## Fixture matrix

| Fixture | Purpose |
| --- | --- |
| `fixtures/Phase0.Net8` | `net8.0` declarations with constants, constructor and named attribute arguments, default and explicit EventName values, escaped placeholders, format specifiers, Unicode, nested and instance types, and explicit unsupported declarations. |
| `fixtures/Phase0.Stable` | The same declaration matrix targeting `net10.0`. |
| `fixtures/Phase0.Multi` | The same source evaluated separately for `net8.0` and `net10.0`. |
| `fixtures/Phase0.Pairing` | Duplicate source identities, ref-kind and generic-arity identity dimensions, and an unpaired generated declaration. The shipping extractor must fail closed rather than guess a pairing. |
| `fixtures/Phase0.Sentinel` | A controlled assembly-load sentinel used only to prove that the sentinel fails if deliberately initialized. |

Each ordinary project contains an execution sentinel. Successful design-time capture demonstrates that the extractor did not load and initialize the target assembly during the matrix. This does not sandbox MSBuild evaluation: projects remain trusted local input, and callers should isolate untrusted projects.

## Shipping assertions

`build/Test-ShippingMatrix.ps1` uses the built shipping assembly and checks that:

- all supported declarations in the ordinary fixtures are captured with the expected effective EventId, EventName, level, placeholder, parameter, nested-type, Unicode, and source-provenance values;
- unsupported declarations remain explicit and the manifest reports incomplete coverage;
- repeated captures are byte-identical;
- the multi-targeted fixture is evaluated independently for each selected target framework;
- ambiguous source/generated pairing returns exit code 3 with `KMLOGP001` and `KMLOGP005` and does not write a baseline.

The installed-tool smoke is a separate gate. It creates a local tool manifest, installs the newly packed `KeelMatrix.LogSchema` package from an isolated feed and cache, copies only the manifest into a fresh checkout directory, runs `dotnet tool restore`, and invokes every command through `dotnet tool run logschema`. Its fail-closed matrix tampers each redundant event-identity dimension independently, including additive values. A packaged type-by-form matrix covers `ILogger`, `LogLevel`, exact `System.Exception`, a non-suffix derived exception, an ordinary custom type, and `string` at every vector position through both `check` and `diff`; invalid rows include symmetric forged-vs-forged comparisons. Every rejected row must return the JSON analysis-error envelope, `coverageComplete: false`, and exit code 3 without an absolute path or stack trace. A separate comparison-originated analysis error proves the same envelope. Exact `System.Exception`, non-suffix derived exception, and ordinary custom-type events are captured and round-trip cleanly; the derived exception keeps its declared type with form `None` and remains excluded from message placeholders by capture-time semantic analysis.

## Reproduce

From the repository root after restore and a Release build:

```powershell
pwsh -NoProfile -File ./build/Test-Phase0Matrix.ps1
pwsh -NoProfile -File ./build/Test-ShippingMatrix.ps1
```

The complete repository gate is:

```powershell
pwsh -NoProfile -File ./build/validate.ps1
```

The public CI workflow runs the complete gate on Windows, Linux, and macOS with the SDK pinned in `global.json`.

## Technical limitations

- Project loading uses design-time MSBuild and Roslyn semantic models. It does not load or invoke target assemblies, but MSBuild project evaluation itself is a local trust boundary.
- V1 support is limited to the documented `LoggerMessageAttribute` declaration shape. Manual logging calls and arbitrary third-party generators are outside scope.
- A multi-targeted project must be evaluated with an explicit `--tfm` when framework selection affects its compilation.
- Compilation errors, workspace failures, ambiguous identities, unsupported declarations during comparison, and zero supported events fail closed through the documented `KMLOGP001`-`KMLOGP007` analysis diagnostics.
