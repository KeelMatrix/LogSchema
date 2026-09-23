# Security policy

## Reporting a vulnerability

Report suspected vulnerabilities privately by emailing `keelmatrix@gmail.com` with the subject `Security report: KeelMatrix.LogSchema`; do not file them as public issues. Include the affected version or commit, operating system and SDK, a minimal synthetic reproduction, impact, and any relevant command-line input. Do not include secrets or customer data.

## Supported versions

The current supported release line is v1. Older versions may receive a security response when a fix can be safely backported, but users should upgrade to the current release before reporting a suspected issue.

## Security boundaries

The tool does not execute target application code, upload source or manifests, or emit telemetry in v1. Project loading still evaluates local MSBuild input; use an appropriate isolation boundary for untrusted projects.

The repository gate parses direct-and-transitive NuGet vulnerability reports and fails on findings, command failures, or unusable audit data. Pack-time targets reject environment, telemetry, credential, key, and internal-development files even when they are explicitly marked for packing, and final archive validation rejects any content outside the expected tool and symbol-package layouts.
