# Security Policy

## Reporting a vulnerability

Report suspected vulnerabilities privately by emailing `keelmatrix@gmail.com` with the subject `Security report: KeelMatrix.LogSchema`; do not file them as public issues. Include the affected version or commit, operating system and SDK, a minimal synthetic reproduction, impact, and any relevant command-line input. Do not include secrets or customer data.

## Supported versions

The intended first supported release line is `0.1.x`, beginning with `0.1.0`. Before the first public package is published, security reports should identify the affected commit. After publication, users should reproduce on the latest available `0.1.x` version before reporting when practical.

## Security boundaries

The tool does not execute target application code, upload source or manifests, or emit telemetry in v1. Manifest input is size- and depth-bounded. The reader parses each declared type with Roslyn, rejects any non-canonical spelling or shape text before classification, and structurally recomputes every parameter form solely from the canonical declared type. It rejects any embedded or redundant mismatch before comparison, including coordinated symmetric and additive mutations. V1 does not trust a manifest to assert arbitrary type-hierarchy semantics. Comparison-time analysis errors also fail closed with incomplete coverage. Project loading still evaluates local MSBuild input; use an appropriate isolation boundary for untrusted projects.

The repository gate parses direct-and-transitive NuGet vulnerability reports and fails on findings, command failures, or unusable audit data. Pack-time targets reject environment, telemetry, credential, key, and internal-development files even when they are explicitly marked for packing, and final archive validation rejects any content outside the expected tool and symbol-package layouts.
