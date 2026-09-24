# Release Process

This file is a navigation pointer. The normative release, merge, npm, issue,
resume, and changelog rules live in [`release_protocol.md`](release_protocol.md).
Read that document before any release operation.

## Fast path

From a clean `main` checkout, after explicit maintainer approval:

```powershell
pwsh -NoProfile -File .\release.ps1 -Version <X.Y.Z> -Detach
```

`-Detach` is recommended for a real release because build, tests, workflow
verification, and registry propagation outlive a short-lived agent terminal.

`release.ps1` is the only publication entrypoint. It commits the exact source
state before building, runs the canonical preflight, creates the manifest and
checksum, packages `publish.zip` and the Nexus VSIX, creates or resumes the
GitHub Release, and waits for `.github/workflows/release.yml` to publish and
verify the exact npm version.

The preflight performs cheap fail-fast checks first, including JSON/Markdown
warning-baseline parity, then runs independent CLI,
PowerShell, Nexus, and non-process solution-test phases in parallel. Tests
that launch Gateways, sockets, or child processes run in a dedicated serial
lane immediately afterward. The MSBuild warning-baseline and live-artifact
phases remain outside that wave: live also invokes `dotnet test` against Gateway
binaries, so sequencing both phases prevents races. The process lane pins the
current Release Gateway/Worker binaries, writes TRX results, and fails if the
filter selects zero tests. Its JSON summary records `sourceCommit`,
`artifactFingerprint`, phase timings, command identity, and the selected test
count.

If a preflight phase fails after the source commit and artifacts were produced,
rerun the same version. The entrypoint resumes only when the version, source
commit, SDK path, live inputs, and artifact fingerprint all match; otherwise it
rebuilds and runs the full gate. Do not manually skip individual phases.

## Verification

`release.ps1` verifies the exact local-versus-remote asset digests and sizes,
non-draft release state, peeled tag commit, tag/event workflow run created
after the current assets, archive bytes against the staged manifest, and the
exact npm package version plus its tagged `gitHead` before it marks the release
successful or closes issues. Evidence is written atomically next to the status
file as `<status>.publication.json`; status/doctor reject missing or mismatched
publication evidence and recursively redact nested diagnostics. Legacy versions
may use the documented `publish.zip`-only recovery path; v3 assets remain
strict.

For a manual diagnosis, use the read-only doctor (add `-Remote` to inspect
GitHub and npm):

```powershell
pwsh -NoProfile -File .\scripts\release-doctor.ps1 -Version <X.Y.Z> -Remote
```

The normal workflow is triggered by the GitHub `published` event only. An
existing release with late assets is repaired through explicit
`workflow_dispatch`; `edited` events do not enqueue duplicate publications.
The workflow keeps npm verification synchronous and exact and reports publish
acceptance, registry visibility, probe count, and propagation seconds in the
GitHub Step Summary. A package becoming visible in the registry can still take
about 90 seconds; that external delay is not removed by local parallelism.

## Recovery

Start with the read-only doctor; it reports the failed phase, artifact
fingerprint, publication state, and the safest root-qualified retry command.
When evaluating a live single-major resume, pass the same KB and fixture
explicitly so it can validate the complete preflight certificate before
suggesting `-SkipBuild -SkipTests`:

```powershell
pwsh -NoProfile -File .\scripts\release-doctor.ps1 -Version <X.Y.Z> `
  -LiveKbPath <C:\KBs\KB> -LiveFixtureManifest <C:\Fixtures\fixture.json> -Remote
```

- Build or preflight failure: keep the same untagged source commit, fix the
  cause, and rerun the same version. Do not guess `-SkipTests` from a log.
- Existing release without complete assets: rerun the same entrypoint; it
  reconciles the complete VSIX/artifact set, uploads the missing assets, and
  dispatches the workflow explicitly instead of creating a duplicate.
- Existing release with complete assets but a failed/missing workflow: rerun
  the same entrypoint; the idempotent verification path resumes publication
  without rebuilding, rewriting the zip/manifest, retagging, or pushing.
- Publication already verified but issue closure was interrupted: rerun with the
  same version; the entrypoint resumes issue closure without a duplicate
  publication.
- Workflow failure after publication: inspect the exact workflow run and npm
  registry state before retrying. The workflow is idempotent for an already
  published exact version.

For issue collection, live SDK/KB evidence, warning baselines, detached status
files, and merge discipline, use `release_protocol.md` and its linked guides.
Contributors should open a PR against `main`; only the maintainer publishes.
Repository text uses LF; `CHANGELOG.md` is the explicit CRLF exception required
by the release merge protocol.
