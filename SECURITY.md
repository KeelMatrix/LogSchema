# Security policy

Please report suspected vulnerabilities privately to the repository maintainers rather than filing public issues. Include the affected version or commit, operating system and SDK, a minimal synthetic reproduction, impact, and any relevant command-line input. Do not include secrets or customer data.

The tool does not execute target application code, upload source or manifests, or emit telemetry in v1. Project loading still evaluates local MSBuild input; use an appropriate isolation boundary for untrusted projects.
