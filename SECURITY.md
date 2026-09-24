# Security Policy

## Reporting a vulnerability

Report suspected vulnerabilities privately by emailing `keelmatrix@gmail.com` with the subject `Security report: KeelMatrix.LogSchema`; do not file them as public issues. Include the affected version or commit, operating system and SDK, a minimal synthetic reproduction, impact, and any relevant command-line input. Do not include secrets or customer data.

## Supported versions

KeelMatrix.LogSchema supports the `0.1.x` release line, beginning with `0.1.0`. Reports about unreleased source builds should identify the affected commit. Users should reproduce on the latest available `0.1.x` version before reporting when practical.

## Security boundaries

The tool does not execute target application code, upload source or manifests, or emit telemetry in v1. LogSchema itself makes no product-owned network requests after the required dependencies are available. Installing or restoring the tool and target project can contact configured package sources, and MSBuild project evaluation remains a local trust boundary: target projects, imports, and tasks may have their own evaluation behavior. Use an appropriate isolation boundary for untrusted projects. Manifest input is size- and depth-bounded. The reader parses each declared type with Roslyn, rejects any non-canonical spelling or shape text before classification, and structurally recomputes every parameter form solely from the canonical declared type. It rejects any embedded or redundant mismatch before comparison, including coordinated symmetric and additive mutations. V1 does not trust a manifest to assert arbitrary type-hierarchy semantics. Comparison-time analysis errors also fail closed with incomplete coverage.

The repository gate parses direct-and-transitive NuGet vulnerability reports and fails on findings, command failures, or unusable audit data. Pack-time targets reject environment, telemetry, credential, key, and internal-development files even when they are explicitly marked for packing, and final archive validation rejects any content outside the expected tool and symbol-package layouts.
