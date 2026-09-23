using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Diagnostics;

namespace GxMcp.Gateway
{
    partial class Program
    {

        internal static bool IsLifecycleBuildDryRun(JObject? args)
        {
            return args?["dryRun"]?.ToObject<bool?>() == true;
        }

        internal static bool ShouldDispatchLifecycleBuildAsync(
            string? toolName, string? lifecycleAction, JObject? args)
        {
            if (!string.Equals(toolName, "genexus_lifecycle", StringComparison.OrdinalIgnoreCase)
                || IsLifecycleBuildDryRun(args))
                return false;

            if (string.Equals(lifecycleAction, "build", StringComparison.OrdinalIgnoreCase)
                || string.Equals(lifecycleAction, "build_all", StringComparison.OrdinalIgnoreCase)
                || string.Equals(lifecycleAction, "rebuild", StringComparison.OrdinalIgnoreCase))
                return true;

            return string.Equals(lifecycleAction, "specify", StringComparison.OrdinalIgnoreCase)
                && args?["wait_until_done"]?.ToObject<bool?>() == true;
        }

        internal static JObject BuildAsyncLifecycleCommand(string lifecycleAction, JObject args, string cancelToken)
        {
            bool rebuild = string.Equals(lifecycleAction, "rebuild", StringComparison.OrdinalIgnoreCase);
            bool buildAll = string.Equals(lifecycleAction, "build_all", StringComparison.OrdinalIgnoreCase);
            bool specify = string.Equals(lifecycleAction, "specify", StringComparison.OrdinalIgnoreCase);
            bool compileCheck = string.Equals(lifecycleAction, "build", StringComparison.OrdinalIgnoreCase)
                && string.Equals(args?["mode"]?.ToString(), "compile_check", StringComparison.OrdinalIgnoreCase);
            var command = new JObject
            {
                ["module"] = "Build",
                ["action"] = specify ? "Specify" : compileCheck ? "CompileCheck" : rebuild ? "RebuildAll" : buildAll ? "BuildAll" : "Build",
                ["target"] = args?["target"]?.ToString(),
                ["client"] = "mcp",
                ["includeCallees"] = args?["includeCallees"]?.ToString(),
                ["buildPlanCap"] = (int?)args?["buildPlanCap"],
                ["skipFullDeploy"] = (bool?)args?["skipFullDeploy"],
                ["callers"] = (bool?)args?["callers"] ?? true,
                ["callerCap"] = (int?)args?["callerCap"] ?? 0,
                ["environment"] = args?["environment"]?.ToString(),
                ["dryRun"] = (bool?)args?["dryRun"] ?? false,
                ["deploy"] = (bool?)args?["deploy"] ?? false,
                ["cancelToken"] = cancelToken
            };
            return command;
        }

        /// <summary>
        /// Gateway-side <c>genexus_lifecycle</c> intercepts: durable-mutation journal
        /// inspect/reconcile, op:&lt;id&gt; status/result/cancel, gateway metrics and the
        /// job long-poll. Returns null for lifecycle actions that must be forwarded
        /// to the Worker (index, verify, legacy taskId paths).
        /// </summary>
        private static async Task<JObject?> HandleLifecycleGatewayInterceptsAsync(
            JToken? idToken, string toolName, JObject? args, JObject request, string sessionId,
            CancellationToken transportCancellation)
        {
            if (string.Equals(toolName, "genexus_lifecycle", StringComparison.OrdinalIgnoreCase))
            {
                string? lifecycleAction = args?["action"]?.ToString();
                string? lifecycleTarget = args?["target"]?.ToString();

                // Durable mutation recovery is intentionally Gateway-local. After a
                // restart the Worker cannot safely infer whether a timed-out write
                // committed, so inspect/reconcile expose only the redacted journal
                // state and require an explicit verification before closing a fence.
                if (string.Equals(lifecycleAction, "inspect", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(lifecycleAction, "reconcile", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        string operationKey = args?["operationKey"]?.ToString()
                            ?? args?["idempotencyKey"]?.ToString()
                            ?? string.Empty;
                        string operationTool = args?["operationTool"]?.ToString()
                            ?? args?["tool"]?.ToString()
                            ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(operationKey) || string.IsNullOrWhiteSpace(operationTool))
                            throw new UsageException("usage_error", "action=inspect|reconcile requires operationKey and operationTool.");
                        IdempotencyMiddleware.ValidateKey(operationKey);

                        string? requestedKb = args?["kb"]?.ToString();
                        string journalKbPath = !string.IsNullOrWhiteSpace(requestedKb)
                            ? (ResolveKbPath(requestedKb) ?? throw new UsageException("kb_not_found", "The requested kb alias/path could not be resolved."))
                            : (_currentKb.Value?.Path ?? _activeConfig?.Environment?.KBPath ?? string.Empty);
                        if (string.IsNullOrWhiteSpace(journalKbPath))
                            throw new UsageException("no_active_kb", "Open or select a KB before inspecting durable operations.");

                        JObject operationPayload;
                        if (string.Equals(lifecycleAction, "inspect", StringComparison.OrdinalIgnoreCase))
                        {
                            operationPayload = _idempotencyCache.InspectOperation(journalKbPath, operationTool, operationKey);
                        }
                        else
                        {
                            bool confirmed = args?["confirmed"]?.ToObject<bool?>() == true;
                            string verification = args?["verification"]?.ToString()
                                ?? args?["verificationToken"]?.ToString()
                                ?? string.Empty;
                            if (!confirmed || string.IsNullOrWhiteSpace(verification))
                                throw new UsageException("verification_required", "Reconcile requires confirmed=true and a non-empty verification statement after an independent genexus_read.");
                            JToken? observedTargets = args?["observedTargetIds"]
                                ?? args?["targetIds"];
                            string? observedRevision = args?["observedRevision"]?.ToString()
                                ?? args?["revision"]?.ToString();
                            var observedEvidence = MutationOperationEvidence.FromObserved(observedTargets, observedRevision);
                            operationPayload = _idempotencyCache.ReconcileOperation(
                                journalKbPath,
                                operationTool,
                                operationKey,
                                verification,
                                observedEvidence);
                        }

                        bool recoveryError = string.Equals(operationPayload["status"]?.ToString(), "Blocked", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(operationPayload["status"]?.ToString(), "Rejected", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(operationPayload["status"]?.ToString(), "NotFound", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(operationPayload["code"]?.ToString(), "operation_journal_unavailable", StringComparison.OrdinalIgnoreCase);
                        return BuildToolTextResponse(idToken, operationPayload, isError: recoveryError, toolName: toolName, toolArgs: args, payloadOwned: true);
                    }
                    catch (UsageException ux)
                    {
                        var usagePayload = new JObject
                        {
                            ["status"] = "Error",
                            ["code"] = ux.Code,
                            ["message"] = ux.Message
                        };
                        return BuildToolTextResponse(idToken, usagePayload, isError: true, toolName: toolName, toolArgs: args, payloadOwned: true);
                    }
                }

                if ((string.Equals(lifecycleAction, "status", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(lifecycleAction, "result", StringComparison.OrdinalIgnoreCase)) &&
                    !string.IsNullOrWhiteSpace(lifecycleTarget) &&
                    lifecycleTarget.StartsWith("op:", StringComparison.OrdinalIgnoreCase))
                {
                    string operationId = lifecycleTarget.Substring(3);
                    // v2.6.2 (Item B follow-up): JobRegistry covers build/edit jobs;
                    // OperationTracker covers gateway-internal request lifecycles.
                    // status/result with op:<id> should resolve in EITHER — fall through
                    // to the JobRegistry long-poll path below when the id is a job.
                    if (JobRegistry.Get(operationId) == null)
                    {
                        JObject opPayload = string.Equals(lifecycleAction, "result", StringComparison.OrdinalIgnoreCase)
                            ? _operationTracker.BuildOperationResult(operationId)
                            : _operationTracker.BuildOperationStatus(operationId);
                        return BuildToolTextResponse(
                            idToken,
                            opPayload,
                            isError: string.Equals(opPayload["status"]?.ToString(), "NotFound", StringComparison.OrdinalIgnoreCase),
                            toolName: "genexus_lifecycle",
                            toolArgs: args,
                            payloadOwned: true);
                    }
                }

                // FR#7 (friction-report 2026-05-14): support best-effort cancellation for
                // op:<id> targets. Previously this fell through to the worker, which only
                // knows about build taskIds, returning "Task ID not found". Now we mark
                // the op as Cancelled in the tracker and abandon any matching pending
                // request. The worker thread may still finish its SDK call, but the
                // client gets a deterministic answer.
                // v2.3.8 (Task 7.2 + post-fix) — cancel via job_id: short-circuit the
                // async pollers (build/edit) by signaling the registered CTS, flip the
                // job to "cancelled", AND fan a Control:Cancel command out to the
                // worker so the in-flight thread-safe handler (search/impact) can
                // stop mid-loop. Without the fan-out the worker kept running its
                // current call to completion while the gateway poller exited.
                if (string.Equals(lifecycleAction, "cancel", StringComparison.OrdinalIgnoreCase))
                {
                    string? cancelJobId = McpRouter.ResolveJobId(args);
                    if (!string.IsNullOrWhiteSpace(cancelJobId) && JobRegistry.Get(cancelJobId!) != null)
                    {
                        var cancellingJob = JobRegistry.Get(cancelJobId!);
                        bool ok = JobRegistry.Cancel(cancelJobId!, "Cancelled by client via lifecycle action=cancel.");
                        // Fire-and-forget worker signal. Thread-safe Control:Cancel runs
                        // on the parallel dispatch path so it interleaves with in-flight calls.
                        _ = SendWorkerCommandAsync(
                            new JObject
                            {
                                ["method"] = "control",
                                ["module"] = "Control",
                                ["action"] = "Cancel",
                                ["params"] = new JObject { ["cancelToken"] = cancelJobId }
                            },
                            5000, "cancel-fanout",
                            env => env,
                            (_, __) => new JObject(),
                            toolName: "genexus_lifecycle", toolArgs: args, trackOperation: false);
                        bool workerRecycled = false;
                        if (ok
                            && cancellingJob?.Kind?.StartsWith("edit/", StringComparison.OrdinalIgnoreCase) == true
                            && !string.IsNullOrWhiteSpace(cancellingJob.WorkerAlias)
                            && _workerPool != null)
                        {
                            try { workerRecycled = _workerPool.RecycleStalledWorker(cancellingJob.WorkerAlias); }
                            catch (Exception recycleEx) { Log($"[AsyncEdit] Cancel recycle failed for job={cancelJobId}: {recycleEx.Message}"); }
                        }
                        if (ok
                            && cancellingJob?.Kind?.StartsWith("edit/", StringComparison.OrdinalIgnoreCase) == true
                            && RequiresAsyncMutationRecovery(cancellingJob))
                        {
                            _mutationRecovery.RequireRead(
                                cancellingJob.WorkerAlias,
                                cancellingJob.Target,
                                cancellingJob.Part,
                                cancelJobId);
                        }
                        // Issue #79: Cancel only acts on running jobs now, so a
                        // terminal job surfaces a truthful message instead of the
                        // misleading "not found in registry".
                        var terminalJob = ok ? null : JobRegistry.Get(cancelJobId);
                        string cancelMsg = ok
                            ? "Job marked Cancelled and Control:Cancel fanned out to the worker. Handlers honouring CancellationToken (search, analyze, build expansion) will terminate within one iteration."
                            : terminalJob != null
                                ? "Job is already in a terminal state ('" + terminalJob.Status + "'); nothing to cancel."
                                : "Job not found in registry (may have completed and been pruned).";
                        var jp = new JObject
                        {
                            ["status"] = ok ? "Cancelled" : "NotFound",
                            ["jobId"] = cancelJobId,
                            ["operationId"] = cancelJobId,
                            ["recycledWorker"] = workerRecycled,
                            ["reReadRequired"] = ok
                                && cancellingJob?.Kind?.StartsWith("edit/", StringComparison.OrdinalIgnoreCase) == true
                                && RequiresAsyncMutationRecovery(cancellingJob),
                            ["message"] = cancelMsg
                        };
                        return BuildToolTextResponse(idToken, jp, isError: !ok, toolName: "genexus_lifecycle", toolArgs: args, payloadOwned: true);
                    }
                }

                if (string.Equals(lifecycleAction, "cancel", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(lifecycleTarget) &&
                    lifecycleTarget.StartsWith("op:", StringComparison.OrdinalIgnoreCase))
                {
                    string operationId = lifecycleTarget.Substring(3);
                    _operationTracker.TryGetContext(operationId, out var cancelledToolName, out var cancelledToolArgs);
                    bool existed = false;
                    // A5: fan a Control:Cancel out to the worker (mirroring the job_id
                    // path above) so cooperative handlers trip their CTS and free the
                    // single STA queue, instead of leaving the worker running the
                    // cancelled op to completion while every later call queues behind it.
                    _ = SendWorkerCommandAsync(
                        new JObject
                        {
                            ["method"] = "control",
                            ["module"] = "Control",
                            ["action"] = "Cancel",
                            ["params"] = new JObject { ["cancelToken"] = operationId }
                        },
                        5000, "cancel-fanout",
                        env => env,
                        (_, __) => new JObject(),
                        toolName: "genexus_lifecycle", toolArgs: args, trackOperation: false);
                    // Try to find and abandon the pending request bound to this op.
                    string? abandonedRequestId = null;
                    string? workerAlias = null;
                    foreach (var kvp in _pendingRequests.ToArray())
                    {
                        if (string.Equals(kvp.Value.OperationId, operationId, StringComparison.OrdinalIgnoreCase))
                        {
                            if (_pendingRequests.TryRemove(kvp.Key, out var pending))
                            {
                                abandonedRequestId = kvp.Key;
                                workerAlias = pending.WorkerAlias;
                                pending.CompletionSource.TrySetResult(JsonConvert.SerializeObject(new
                                {
                                    jsonrpc = "2.0",
                                    id = kvp.Key,
                                    error = new { code = -32603, message = "Operation cancellation requested by client; the SDK call may still be running." }
                                }));
                                break;
                            }
                        }
                    }

                    bool workerRecycled = false;
                    if (!string.IsNullOrWhiteSpace(workerAlias) && _workerPool != null)
                    {
                        try { workerRecycled = _workerPool.RecycleStalledWorker(workerAlias); }
                        catch (Exception recycleEx) { Log($"[Operation] Cancel recycle failed for op={operationId}: {recycleEx.Message}"); }
                    }
                    existed = _operationTracker.MarkCancelled(operationId,
                        workerRecycled
                            ? "Cancelled by client; the non-preemptible worker was recycled. Re-read the object before another write."
                            : "Cancelled by client; no live worker remained to recycle. Re-read the object before another write.");
                    if (existed
                        && (IsAsyncMutationTool(cancelledToolName)
                            || IsAsyncGxServerAction(cancelledToolName, cancelledToolArgs))
                        && !string.IsNullOrWhiteSpace(workerAlias))
                    {
                        foreach (var recoveryTarget in EnumerateMutationRecoveryTargets(cancelledToolName!, cancelledToolArgs))
                            _mutationRecovery.RequireRead(workerAlias, recoveryTarget.Target, recoveryTarget.Part, operationId);
                    }

                    var cancelPayload = new JObject
                    {
                        ["status"] = existed ? "Cancelled" : "NotFound",
                        ["operationId"] = operationId,
                        ["abandonedRequestId"] = abandonedRequestId,
                        ["recycledWorker"] = workerRecycled,
                        ["reReadRequired"] = existed,
                        ["message"] = existed
                            ? "Operation reached terminal Cancelled state. The worker was recycled when it was still executing the non-preemptible SDK call; re-read the target before another write."
                            : "Operation not found in tracker (may have completed and been pruned, or never existed)."
                    };
                    return BuildToolTextResponse(idToken, cancelPayload, isError: !existed, toolName: "genexus_lifecycle", toolArgs: args, payloadOwned: true);
                }

                if (string.Equals(lifecycleAction, "status", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(lifecycleTarget, "gateway:metrics", StringComparison.OrdinalIgnoreCase))
                {
                    return BuildToolTextResponse(idToken, _operationTracker.BuildMetricsPayload(), isError: false, toolName: "genexus_lifecycle", toolArgs: args);
                }

                // action=result + op:<jobId> — return the stored JobEntry.Result directly.
                // v2.6.3 fixed status/cancel for op:<id> via JobRegistry but result still
                // forwarded to the worker, which only knows about its internal taskId and
                // returned "Task ID not found" for completed jobs (visible in
                // _meta.background_jobs). Symmetric handler closes that gap.
                if (string.Equals(lifecycleAction, "result", StringComparison.OrdinalIgnoreCase))
                {
                    string? resultJobId = McpRouter.ResolveJobId(args);
                    if (!string.IsNullOrWhiteSpace(resultJobId))
                    {
                        var probe = JobRegistry.Get(resultJobId);
                        if (probe != null)
                        {
                            // Issue #27 item 1: if the job is still "running", actively
                            // reconcile against the worker before reporting Pending — the
                            // background poller may have wedged.
                            await ReconcileJobWithWorkerAsync(probe, "genexus_lifecycle", args);
                            // Envelope shape extracted into McpRouter.BuildJobResultEnvelope
                            // for unit-test coverage and parity with status long-poll.
                            var (resultPayload, isErr) = McpRouter.BuildJobResultEnvelope(probe);
                            return BuildToolTextResponse(idToken, resultPayload, isError: isErr, toolName: "genexus_lifecycle", toolArgs: args);
                        }
                        // Unknown id → fall through to the legacy worker-side taskId result path.
                    }
                }

                // Long-poll intercept (Task 4.5): action=status + job_id (BackgroundJobRegistry)
                // wait_seconds is clamped [0, MaxLongPollSeconds]; 0 = immediate poll (default behaviour).
                if (string.Equals(lifecycleAction, "status", StringComparison.OrdinalIgnoreCase))
                {
                    string? jobId = McpRouter.ResolveJobId(args);
                    if (!string.IsNullOrWhiteSpace(jobId))
                    {
                        // Check registry first; only route to the long-poll path when the job
                        // is known to the registry. Unknown IDs fall through to the legacy
                        // worker-side taskId status path for backward compatibility.
                        var probe = JobRegistry.Get(jobId);
                        if (probe != null)
                        {
                            // Issue #27 item 1: reconcile a still-running job against the
                            // worker's real build-task state before long-polling, so a wedged
                            // background poller can't keep a finished build stuck at "running".
                            await ReconcileJobWithWorkerAsync(probe, "genexus_lifecycle", args);
                            int waitSeconds = Math.Min(Math.Max(args?["wait_seconds"]?.ToObject<int?>() ?? 0, 0), McpRouter.MaxLongPollSeconds);
                            var clientProgressToken = (request["params"] as JObject)?["_meta"]?["progressToken"];
                            bool hasProgressToken = clientProgressToken != null && clientProgressToken.Type != JTokenType.Null;
                            string pendingLongPollKey = RegisterPendingLongPoll(
                                sessionId,
                                idToken,
                                transportCancellation,
                                out var longPollCancellationToken);
                            JObject pollResult;
                            try
                            {
                                pollResult = await McpRouter.LongPollJob(
                                    JobRegistry, jobId, waitSeconds,
                                    progressToken: clientProgressToken,
                                    heartbeat: hasProgressToken ? TryWriteStdout : null,
                                    cancellationToken: longPollCancellationToken);
                            }
                            finally
                            {
                                UnregisterPendingLongPoll(pendingLongPollKey);
                            }
                            bool isError = pollResult["error"] != null;
                            // Friction 2026-05-22 item 10: a "Build succeeded: 0w/0e/exit=0"
                            // result was previously dropped onto an <e>error{}> envelope when
                            // the JobEntry.Status string didn't match what callers expected.
                            // Run the build-outcome classifier on the inner BuildTaskStatus
                            // (if present) so the envelope's isError matches the actual outcome.
                            if (!isError && pollResult["result"] is JObject buildPayloadEarly)
                            {
                                var outcome = LifecycleResponseShaper.ClassifyBuildOutcome(buildPayloadEarly);
                                if (outcome == LifecycleResponseShaper.BuildOutcome.Error) isError = true;
                                else if (outcome == LifecycleResponseShaper.BuildOutcome.PartialSuccess)
                                {
                                    // Surface partial_success on the outer envelope as a warning marker.
                                    pollResult["partial_success"] = true;
                                    if (pollResult["envelope"] == null) pollResult["envelope"] = "warning";
                                }
                                else if (outcome == LifecycleResponseShaper.BuildOutcome.Success)
                                {
                                    // Defensive: a job stamped "failed" by the registry but whose
                                    // BuildTaskStatus says Succeeded/0/0/exit=0 should not be an error.
                                    // (We've seen this race: registry summary stamped before final
                                    // status normalized.) Keep isError=false in that case.
                                }
                            }
                            // v2.3.8 (post-Task 6.1 fix): the DispatchCore compact pass below
                            // only runs for the worker-side legacy taskId path. job_id results
                            // arrive through this short-circuit and were skipping the shaper —
                            // callers using wait_seconds>0 + job_id were getting the verbose
                            // BuildTaskStatus payload under result. Compact here too.
                            if (!isError
                                && LifecycleResponseShaper.ShouldCompact(args)
                                && pollResult["result"] is JObject innerResult)
                            {
                                try { pollResult["result"] = LifecycleResponseShaper.CompactObject(innerResult); } // perf: no serialize→parse round-trip
                                catch { /* shaper passthrough on non-JSON */ }
                            }
                            return BuildToolTextResponse(idToken, pollResult, isError: isError, toolName: "genexus_lifecycle", toolArgs: args);
                        }
                        // Not in registry → fall through to existing worker-side status path (legacy taskId).
                    }
                }
                }
            return null;
        }
    }
}
