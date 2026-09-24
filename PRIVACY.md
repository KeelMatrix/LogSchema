# Privacy

KeelMatrix.LogSchema analyzes local source projects and manifests. Version `0.1.x` contains no telemetry client, does not upload source or manifest data, and does not require a hosted service.

## Local data

`capture` reads design-time project and source information through MSBuild/Roslyn and writes the requested manifest. `check` reads the target project and baseline. `diff` reads only the two supplied manifests. The tool may create a temporary manifest file beside the requested output while completing an atomic write; it removes that temporary file and does not create a background service.

Manifests contain developer-selected operational vocabulary, including project and assembly names, type and method identities, event names, templates, placeholder names, and logical source locations. Source locations use deterministic `project/`, `external/up-N/`, or `generated/` identities without absolute checkout roots; review a manifest like source code before committing or sharing it.

## Network behavior

LogSchema itself makes no product-owned network requests during `capture`, `check`, or `diff` after the required dependencies are available. Installing or restoring the tool and target project can contact the package sources configured for the .NET SDK. `capture` and `check` invoke MSBuild project evaluation, and target projects, imports, or tasks may have their own network behavior; this is a local trust boundary rather than a LogSchema network service. `diff` only reads existing manifests locally.

The `--no-telemetry` option is accepted for script portability. It does not change v1 behavior because no telemetry code is included.

## Project-loading boundary

The tool does not load or invoke the target application assembly and does not inspect runtime log values. MSBuild project evaluation can execute project-system logic, so a project remains trusted local input. Use an appropriate isolation boundary before evaluating an untrusted project.

See [MANIFEST.md](MANIFEST.md) for the persisted data contract and [SECURITY.md](SECURITY.md) for vulnerability reporting and security boundaries.
