# Project-analysis resource gate

LogSchema uses design-time MSBuild and Roslyn to inspect SDK-style C# projects. The release-equivalent validation includes a reproducible shipping-tool probe for the project-loading side of that boundary:

```powershell
pwsh -NoProfile -NonInteractive -File ./build/Test-ProjectAnalysisResources.ps1 `
  -RepositoryRoot (Resolve-Path .).Path
```

The probe creates its fixture under `artifacts/validation/` and removes it on the next run. It performs a controlled restore with the repository `NuGet.config`, then runs the Release-built shipping DLL, not a test-only extractor.

## Fixture and limits

The normal fixture contains 24 SDK-style `net8.0` projects arranged as a project-reference graph, 16 C# source documents per project, and four `[LoggerMessage]` declarations per document: 24 projects, 384 source documents, and 1,536 declarations. The generated solution is a classic `.sln` because it is the solution format supported by the shipping Roslyn/MSBuild workspace on the verified SDK.

The extractor fails closed before semantic scanning when the loaded graph exceeds any of these product limits:

- 64 C# projects in one solution;
- 512 project documents;
- 4,096 source documents across the input.

These are safety ceilings, not performance promises. An input above a ceiling returns exit code 3 with a `Project analysis resource limit exceeded` error. MSBuild evaluation remains a local trust boundary; callers should isolate untrusted projects because a target project or imported MSBuild logic can have behavior outside LogSchema's own code.

The probe also restores and analyzes two pathological inputs: a 65-project solution and a one-project source graph with 513 documents. Both must fail clearly with exit code 3 without exceeding the 120-second bounded-failure allowance.

## Measured CI budget

The baseline below was measured after controlled restore on the Windows development runner using the Release shipping implementation:

| Input | Projects | Documents | Declarations | Elapsed | Peak working set | Peak private memory |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| resource fixture | 24 | 384 | 1,536 | 18.49s | 274.0 MiB | 160.6 MiB |

The checked-in CI budget is 120 seconds, 1,536 MiB peak working set, and 1,024 MiB peak private memory. That gives approximately 6.5× elapsed, 5.6× working-set, and 6.4× private-memory headroom over the measured run. The values are a release-validation safety budget with runner variance, not a published benchmark or throughput claim. The same gate runs from `build/validate.ps1` on each Windows, Linux, and macOS CI leg; its output is the evidence for the runner on which it executes.
