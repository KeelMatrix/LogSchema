# Compatibility rules

This file is the single source of truth for v1 change classification and diagnostic codes. A comparison matches events by canonical project key and method identity. A method identity change therefore appears as one removed event and one added event; removal is the breaking side of that change.

Structured identity fields use exact ordinal semantics: comparisons are case-sensitive and culture-invariant. This applies to canonical project and method identities, EventName, and structured placeholder names and order. A case-only placeholder change is therefore a structured rename (`KMLOG102`), not prose-only drift. `KMLOG301` applies only when the structured shape is unchanged under these ordinal comparisons.

| Code | Change | Severity | Gated by default | Notes |
| --- | --- | --- | --- | --- |
| KMLOG001 | Event added | INFO | No | Additions are visible but compatible. |
| KMLOG002 | Event removed | BREAKING | Yes | Includes the removed side of an identity change. |
| KMLOG003 | EventId changed | BREAKING | Yes | The logical event identity changed. |
| KMLOG004 | EventName changed | BREAKING | Yes | Includes a change from or to the framework default method name. |
| KMLOG101 | Structured placeholder removed | BREAKING | Yes | Removing a structured field breaks consumers. |
| KMLOG102 | Structured placeholder renamed | BREAKING | Yes | A one-for-one removed/added name pair is reported as a rename. |
| KMLOG103 | Structured placeholder order changed | BREAKING | Yes | Order is part of the supported source-generated declaration shape. |
| KMLOG104 | Structured placeholder added | INFO | No | Additions are visible but compatible. |
| KMLOG201 | LogLevel changed | WARNING | No | Use `--severity warning` to gate warnings. |
| KMLOG301 | Template prose changed with unchanged structured shape | INFO | No | Includes formatting-token changes. |

`--severity breaking` gates only BREAKING findings. `--severity warning` gates BREAKING and WARNING findings. `--severity all` gates every finding, including INFO. Findings remain structured and stable in text and JSON output. `--accept <code>` explicitly accepts matching findings for the current comparison and never rewrites a baseline.

## Analysis diagnostics

| Code | Condition | Result |
| --- | --- | --- |
| KMLOGP006 | No supported `[LoggerMessage]` declarations were found. | `capture` and `check` return exit 3; `capture` writes no baseline and `check` rejects a zero-event baseline. |

Rules apply to C# methods using `Microsoft.Extensions.Logging.LoggerMessageAttribute` within the documented v1 supported declaration scope. Manual logging calls, runtime values, providers, and arbitrary third-party generators are not compared.
