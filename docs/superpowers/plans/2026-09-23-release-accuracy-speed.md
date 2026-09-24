# Release Accuracy and Speed Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the release pipeline deterministic, safely resumable, automatically verified after publication, and quick to diagnose without weakening the existing provenance or fail-closed gates.

**Architecture:** Keep `release.ps1` as the single publication entrypoint. Add a process-sensitive test lane to the preflight, make `-SkipBuild` restore and verify the complete artifact set, add a publication verifier for GitHub Release/workflow/npm, and expose local/remote recovery diagnostics through `release-doctor.ps1` and the status file. Generate warning documentation from the JSON baseline and enforce the repository's existing line-ending policy: LF for normal text, with the release protocol's explicit CRLF exception for `CHANGELOG.md`.

**Tech Stack:** PowerShell 7 release/test scripts, Python validation scripts, xUnit/.NET 10 Gateway tests, .NET Framework Worker tests, GitHub Actions YAML, Git attributes and EditorConfig.

---

### Task 1: Establish deterministic process-test scheduling

**Files:**
- Modify: `scripts/release-preflight.ps1`
- Modify: `src/GxMcp.Gateway.Tests/Issue146AcceptanceMatrixContractTests.cs`
- Modify: `src/GxMcp.Gateway.Tests/McpSmokeScriptContractTests.cs`
- Modify: `src/GxMcp.Gateway.Tests/GatewayProcessLeaseTests.cs`
- Modify: `src/GxMcp.Gateway.Tests/LiveGatewayHarnessCleanupTests.cs`
- Modify: `src/GxMcp.Gateway.Tests/E2ELiveSmokeTests.cs`
- Modify: `src/GxMcp.Worker.Tests/BuildServiceTests.cs`
- Modify: `src/GxMcp.Worker.Tests/BuildReapByPidTests.cs`
- Modify: `src/GxMcp.Worker.Tests/GithubServiceTests.cs`
- Modify: `src/GxMcp.Worker.Tests/TimeTravelServiceTests.cs`
- Modify: `scripts/tests/test-release-preflight.ps1`

- [x] **Step 1: Add a `ProcessSmoke` xUnit trait to every test class that starts child processes or live harnesses.** Use `[Trait("Category", "ProcessSmoke")]` at class level; preserve existing `LiveE2E` traits where present.
- [x] **Step 2: Split the solution phase in `release-preflight.ps1`.** Keep the existing solution build/test phase but add `Category!=ProcessSmoke`; then run a second `dotnet test` phase with `--no-build --no-restore --filter Category=ProcessSmoke` after the parallel wave and before the warning baseline/live gate.
- [x] **Step 3: Keep the live gate last and update phase comments/docs so the preflight no longer claims every non-warning phase is safe to overlap.** The process phase must never be in `$parallelDefinitions`.
- [x] **Step 4: Extend `test-release-preflight.ps1` to assert the new phase order, category filters, and that the process phase is after the parallel coordinator.** Update expected dry-run phase counts and names.
- [x] **Step 5: Run the focused PowerShell preflight test and the Gateway/Worker test assemblies with the new filter.** Expected: all pass, with process tests executing in the dedicated phase.

### Task 2: Make `-SkipBuild` artifact reuse complete and fail-closed

**Files:**
- Modify: `release.ps1`
- Modify: `scripts/tests/test-release-orchestration.ps1`
- Modify: `scripts/tests/test-release-entrypoint.ps1`

- [x] **Step 1: Add a production helper that reconciles `nexus-ide-$Version.vsix` and `publish/nexus-ide.vsix`.** Accept either existing location, copy the missing side, and fail if both exist with different SHA-256 hashes.
- [x] **Step 2: Invoke the helper after both build and skip-build paths, before fingerprint/preflight and before release asset assembly.** Require all four fingerprint artifacts (`Gateway`, `Worker`, tool definitions, VSIX) to exist; never silently publish with a missing VSIX.
- [x] **Step 3: Preserve the existing source-commit and artifact-fingerprint checks.** A `-SkipBuild` retry may reuse only a complete, matching artifact set.
- [x] **Step 4: Add hermetic tests using temporary root/publish VSIX files for missing-root, missing-publish, matching, and mismatched-hash cases.** Add a source-contract assertion that the skip path calls the helper.
- [x] **Step 5: Run `test-release-orchestration.ps1`, `test-release-entrypoint.ps1`, and the full PowerShell release-script suite.**

### Task 3: Add publication verification and a diagnostic evidence record

**Files:**
- Create: `scripts/verify-release-publication.ps1`
- Modify: `release.ps1`
- Modify: `scripts/release-status.ps1`
- Create: `scripts/tests/test-release-publication.ps1`
- Modify: `scripts/tests/test-release-status.ps1`
- Modify: `docs/release_protocol.md`
- Modify: `docs/RELEASE.md`

- [x] **Step 1: Implement bounded verification helpers for GitHub Release assets, the peeled tag commit, the exact workflow run, and exact npm package version.** Reuse bounded npm probes (`--fetch-retries=0`, finite timeout) and do not sleep after the final attempt.
- [x] **Step 2: Write an atomic publication evidence JSON containing the tag, expected/source commit, asset names, workflow ID/status/conclusion, npm version, and timestamps.** Update it as each check progresses and on failure.
- [x] **Step 3: Invoke the verifier from `release.ps1` after the GitHub Release exists and before issue mutation.** Wait for the release workflow; use manual dispatch only for an existing-release resume after a bounded discovery grace period. Do not publish npm locally.
- [x] **Step 4: Change issue closure ordering so comment/close/verify happens only after publication verification succeeds.** Keep the existing prevalidation and immutable issue snapshot.
- [x] **Step 5: Extend release status with publication state, evidence path, next action, artifact fingerprint, and retry guidance. Add `-Json` and publication fields to `release-status.ps1`.
- [x] **Step 6: Add hermetic tests for exact-version comparison, required asset names, evidence shape, and status rendering. Add source assertions that `release.ps1` invokes verification before `Close-ReleaseIssues`.
- [x] **Step 7: Run the publication/status PowerShell tests and `test-release-orchestration.ps1`.**

### Task 4: Prevent duplicate release workflow runs

**Files:**
- Modify: `.github/workflows/release.yml`
- Modify: `scripts/tests/test-release-workflow.ps1`
- Modify: `docs/release_protocol.md`
- Modify: `docs/RELEASE.md`

- [x] **Step 1: Change the normal release trigger to `published` only.** Keep `workflow_dispatch` as the explicit asset-repair/retry path.
- [x] **Step 2: Update the workflow comment and release documentation to explain manual dispatch for an existing release whose assets were uploaded after creation.
- [x] **Step 3: Extend the workflow contract test to reject `edited` in the normal trigger set and require the manual-dispatch path and exact npm idempotence checks.
- [x] **Step 4: Run `test-release-workflow.ps1` and validate the YAML parses with the repository's available tooling.**

### Task 5: Generate and validate warning-baseline documentation

**Files:**
- Modify: `scripts/check-build-warning-baseline.ps1`
- Modify: `docs/build_warning_baseline.md`
- Modify: `scripts/tests/test-warning-baseline.ps1`
- Modify: `docs/release_protocol.md`

- [x] **Step 1: Add a deterministic generated block to the Markdown baseline containing the generated date, warning count, project/code totals, and total row.** Keep policy prose outside the block.
- [x] **Step 2: Add documentation-path support to `-UpdateBaseline`, writing JSON and Markdown atomically from the same distinct-warning collection.**
- [x] **Step 3: Make `-ValidateOnly` and normal validation reject a missing marker, stale generated block, mismatched total, or mismatched project/code table.** Keep the JSON as the source of truth.
- [x] **Step 4: Add hermetic tests for generated rendering and a deliberately stale Markdown fixture.** Keep the existing move-aware warning comparison tests.
- [x] **Step 5: Run `test-warning-baseline.ps1`, `check-build-warning-baseline.ps1 -ValidateOnly`, and the release script tests.**

### Task 6: Add cross-boundary regression coverage

**Files:**
- Modify: `src/GxMcp.Gateway.Tests/PropertiesRouterBatchTests.cs` or the existing Gateway router contract test file
- Modify: `src/GxMcp.Worker.Tests/WriteVerificationIntegrityTests.cs`
- Add/modify focused tests only if the existing files do not expose the required public boundary.

- [x] **Step 1: Exercise `genexus_properties action=get` with multiple `targets[]` through the Gateway router/dispatcher boundary and assert the Worker request retains order and values.**
- [x] **Step 2: Add table-driven persistence verification cases for `Variables`, `Structure`, `Source`, and `Events`, covering default tolerant verification and explicit `verifyMode=exact`.**
- [x] **Step 3: Run the narrow Gateway and Worker test filters first, then the complete solution tests.** Expected: no live SDK dependency for these regressions.

### Task 7: Add fast release recovery diagnostics

**Files:**
- Create: `scripts/release-doctor.ps1`
- Create: `scripts/tests/test-release-doctor.ps1`
- Modify: `scripts/release-status.ps1`
- Modify: `docs/RELEASE.md`
- Modify: `docs/release_protocol.md`

- [x] **Step 1: Implement a read-only doctor that resolves version, local HEAD/branch, status file, preflight summary, artifact presence/hashes, tag state, and (with `-Remote`) workflow/npm state.** It must never mutate Git, GitHub, npm, or KB state.
- [x] **Step 2: Derive a safe `nextAction` and `retryCommand` from the persisted status rather than guessing from log text.** Never recommend `-SkipTests` unless the matching preflight summary and artifact fingerprint prove it is safe.
- [x] **Step 3: Support human-readable and `-Json` output, with actionable missing-artifact and stale-summary messages.
- [x] **Step 4: Add fixture-based tests for no-status, failed-preflight, post-build failure, and verified-publication states, plus source assertions for redaction and read-only behavior.
- [x] **Step 5: Add the doctor command to the detached release quick-start and recovery instructions.**

### Task 8: Enforce the repository line-ending policy

**Files:**
- Create: `.gitattributes`
- Modify: `.editorconfig`
- Create: `scripts/tests/test-line-endings.ps1`
- Modify: `scripts/tests/run-release-script-tests.ps1`
- Modify: `docs/release_protocol.md`

- [x] **Step 1: Use the repository's existing `.editorconfig` LF default for normal text and explicitly preserve CRLF for `CHANGELOG.md`, matching the release merge protocol and current tracked history.**
- [x] **Step 2: Add Git attributes that enforce LF for normal text and CRLF for the changelog exception without changing binary handling.**
- [x] **Step 3: Add tests that inspect `git check-attr` and EditorConfig policy for `CHANGELOG.md`, Markdown, PowerShell, and a representative source file.**
- [x] **Step 4: Do not rewrite unrelated historical files; document the intentional exception in the release protocol.**

### Task 9: Changelog, documentation, and final verification

**Files:**
- Modify: `CHANGELOG.md` under `## Unreleased`
- Modify: `docs/mcp_capabilities_inventory.md` only if a published action/schema changes (none expected)
- Modify: `docs/operation-contract-inventory.json` only if generated contract output changes (verify with `--check`)

- [x] **Step 1: Add one concise `### Internal` changelog entry describing deterministic release validation, safe resume, publication verification, and line-ending policy.**
- [x] **Step 2: Run the focused PowerShell release suite, Gateway/Worker tests, CLI tests/lint, Python script tests, and release-contract checks.**
- [x] **Step 3: Run `python scripts/validate-tool-contracts.py` and `python scripts/generate-operation-contract-inventory.py --check` to confirm no tool contract drift.**
- [x] **Step 4: Review `git diff --check`, `git status`, and the final diff for scope, secrets, generated files, and unintended line-ending churn.**
- [x] **Step 5: Only after fresh evidence passes, report completion; do not commit, push, tag, publish, or close issues unless separately requested.**

---

## Self-review checklist

- [x] Every requested release-accuracy improvement has an implementation task and regression test.
- [x] The process lane preserves meaningful parallelism without overlapping process-sensitive tests.
- [x] Resume safety keeps exact source/artifact provenance checks fail-closed.
- [x] Publication success means GitHub assets, workflow completion, and exact npm visibility—not merely `gh release create` returning.
- [x] Existing warning JSON remains the source of truth; Markdown is generated and checked.
- [x] The chosen line-ending policy is consistent with `.editorconfig`, Git history, and the release merge contract.
- [x] No task requires bypassing tests, weakening fingerprints, or publishing directly from the local machine.
