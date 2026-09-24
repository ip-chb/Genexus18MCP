using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public enum CommandPriority
    {
        P0_Interactive = 0,
        P1_Normal = 1,
        P2_Background = 2
    }

    public sealed class ScheduledCommandItem
    {
        public JObject Obj { get; set; }
        public string RawLine { get; set; }
        public CommandPriority Priority { get; set; }
        public string ClientId { get; set; }
        public DateTime EnqueuedAtUtc { get; set; }
        public int BusyWaitMs { get; set; }
        public string IdJson { get; set; }
        public string Method { get; set; }
        public string Action { get; set; }
    }

    public sealed class StaScheduler
    {
        private static readonly Lazy<StaScheduler> _instance =
            new Lazy<StaScheduler>(() => new StaScheduler(), LazyThreadSafetyMode.ExecutionAndPublication);

        public static StaScheduler Instance => _instance.Value;

        private readonly object _lock = new object();

        // Queues per priority, mapped by ClientId for fair round-robin scheduling
        private readonly Dictionary<string, Queue<ScheduledCommandItem>> _p0Queues =
            new Dictionary<string, Queue<ScheduledCommandItem>>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _p0ClientOrder = new List<string>();
        private int _p0Cursor;

        private readonly Dictionary<string, Queue<ScheduledCommandItem>> _p1Queues =
            new Dictionary<string, Queue<ScheduledCommandItem>>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _p1ClientOrder = new List<string>();
        private int _p1Cursor;

        private readonly Dictionary<string, Queue<ScheduledCommandItem>> _p2Queues =
            new Dictionary<string, Queue<ScheduledCommandItem>>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _p2ClientOrder = new List<string>();
        private int _p2Cursor;

        public static CommandPriority ResolvePriority(JObject obj, string line = null)
        {
            if (obj == null && !string.IsNullOrEmpty(line))
            {
                try { obj = GxMcp.Common.JsonIngress.ParseObject(line); } catch { }
            }
            if (obj == null) return CommandPriority.P1_Normal;

            // 1. Explicit priority override
            string explicitPrio = obj["_meta"]?["priority"]?.ToString() ?? obj["params"]?["priority"]?.ToString();
            if (!string.IsNullOrEmpty(explicitPrio))
            {
                if (string.Equals(explicitPrio, "p0", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(explicitPrio, "interactive", StringComparison.OrdinalIgnoreCase))
                    return CommandPriority.P0_Interactive;
                if (string.Equals(explicitPrio, "p2", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(explicitPrio, "background", StringComparison.OrdinalIgnoreCase))
                    return CommandPriority.P2_Background;
                return CommandPriority.P1_Normal;
            }

            string method = obj["method"]?.ToString()?.ToLowerInvariant() ?? "";
            string action = (obj["action"]?.ToString() ?? obj["params"]?["action"]?.ToString())?.ToLowerInvariant() ?? "";

            // P0: Exact reads, inspect, properties get, lightweight status/whoami
            if (method == "object")
            {
                if (action == "extractsource" || action == "extractparts" ||
                    action == "extractfullobject" || action == "getvariables" ||
                    action == "getattribute")
                    return CommandPriority.P0_Interactive;
            }
            if (method == "inspect" || method == "whoami")
                return CommandPriority.P0_Interactive;
            if (method == "properties" && (action == "get" || string.IsNullOrEmpty(action)))
                return CommandPriority.P0_Interactive;
            if (method == "kb" && (action == "list" || action == "getindexstatus" || action == "getindexstate"))
                return CommandPriority.P0_Interactive;
            if (method == "history" && (action == "list" || action == "read"))
                return CommandPriority.P0_Interactive;
            if (method == "structure" && (action == "getlogicstructure" || action == "getvisualstructure"))
                return CommandPriority.P0_Interactive;

            // P2: Background scans, unscoped source search, doc generation
            if (method == "search" && action == "searchsource")
                return CommandPriority.P2_Background;
            if (method == "doc")
                return CommandPriority.P2_Background;

            // P1: Writes, compilations, validations, refactors, creates
            return CommandPriority.P1_Normal;
        }

        public static int ResolveBusyWaitMs(JObject obj)
        {
            if (obj != null)
            {
                var val = obj["params"]?["busyWaitMs"] ?? obj["_meta"]?["busyWaitMs"];
                if (val != null && int.TryParse(val.ToString(), out int parsed) && parsed > 0)
                    return parsed;
            }
            var env = Environment.GetEnvironmentVariable("GXMCP_BUSY_WAIT_MS");
            if (int.TryParse(env, out int envParsed) && envParsed > 0)
                return envParsed;

            // Default bounded wait: 15 seconds
            return 15000;
        }

        public static string ResolveClientId(JObject obj)
        {
            if (obj == null) return "default";
            string id = obj["_meta"]?["clientId"]?.ToString()
                ?? obj["_meta"]?["attachmentId"]?.ToString()
                ?? obj["_meta"]?["sessionId"]?.ToString()
                ?? obj["params"]?["clientId"]?.ToString();
            return string.IsNullOrEmpty(id) ? "default" : id;
        }

        public void Enqueue(ScheduledCommandItem item)
        {
            if (item == null) return;
            string client = string.IsNullOrEmpty(item.ClientId) ? "default" : item.ClientId;

            lock (_lock)
            {
                switch (item.Priority)
                {
                    case CommandPriority.P0_Interactive:
                        EnqueueInternal(_p0Queues, _p0ClientOrder, client, item);
                        break;
                    case CommandPriority.P1_Normal:
                        EnqueueInternal(_p1Queues, _p1ClientOrder, client, item);
                        break;
                    case CommandPriority.P2_Background:
                        EnqueueInternal(_p2Queues, _p2ClientOrder, client, item);
                        break;
                }
            }
        }

        private static void EnqueueInternal(Dictionary<string, Queue<ScheduledCommandItem>> dict,
            List<string> order, string client, ScheduledCommandItem item)
        {
            if (!dict.TryGetValue(client, out var q))
            {
                q = new Queue<ScheduledCommandItem>();
                dict[client] = q;
                order.Add(client);
            }
            q.Enqueue(item);
        }

        public bool HasPendingInteractive
        {
            get
            {
                lock (_lock)
                {
                    return _p0Queues.Values.Any(q => q.Count > 0);
                }
            }
        }

        public bool TryTakeNext(out ScheduledCommandItem item)
        {
            lock (_lock)
            {
                if (TryTakeRoundRobin(_p0Queues, _p0ClientOrder, ref _p0Cursor, out item))
                    return true;
                if (TryTakeRoundRobin(_p1Queues, _p1ClientOrder, ref _p1Cursor, out item))
                    return true;
                if (TryTakeRoundRobin(_p2Queues, _p2ClientOrder, ref _p2Cursor, out item))
                    return true;

                item = null;
                return false;
            }
        }

        public bool TryTakeInteractive(out ScheduledCommandItem item)
        {
            lock (_lock)
            {
                return TryTakeRoundRobin(_p0Queues, _p0ClientOrder, ref _p0Cursor, out item);
            }
        }

        public int DrainPendingInteractive(Action<ScheduledCommandItem> processor)
        {
            if (processor == null) return 0;
            int count = 0;
            while (TryTakeInteractive(out var item))
            {
                count++;
                processor(item);
            }
            return count;
        }

        public List<ScheduledCommandItem> ExpireTimedOut(DateTime nowUtc)
        {
            var expired = new List<ScheduledCommandItem>();
            lock (_lock)
            {
                ExpireFromQueue(_p0Queues, _p0ClientOrder, nowUtc, expired);
                ExpireFromQueue(_p1Queues, _p1ClientOrder, nowUtc, expired);
                ExpireFromQueue(_p2Queues, _p2ClientOrder, nowUtc, expired);
            }
            return expired;
        }

        private static void ExpireFromQueue(Dictionary<string, Queue<ScheduledCommandItem>> dict,
            List<string> order, DateTime nowUtc, List<ScheduledCommandItem> expired)
        {
            foreach (var kvp in dict)
            {
                var q = kvp.Value;
                int count = q.Count;
                for (int i = 0; i < count; i++)
                {
                    var item = q.Dequeue();
                    double ageMs = (nowUtc - item.EnqueuedAtUtc).TotalMilliseconds;
                    if (item.BusyWaitMs > 0 && ageMs > item.BusyWaitMs)
                    {
                        expired.Add(item);
                    }
                    else
                    {
                        q.Enqueue(item);
                    }
                }
            }
        }

        private static bool TryTakeRoundRobin(Dictionary<string, Queue<ScheduledCommandItem>> dict,
            List<string> order, ref int cursor, out ScheduledCommandItem item)
        {
            item = null;
            if (order.Count == 0) return false;

            int checkedCount = 0;
            while (checkedCount < order.Count)
            {
                if (cursor >= order.Count) cursor = 0;
                string client = order[cursor];
                cursor++;
                checkedCount++;

                if (dict.TryGetValue(client, out var q) && q.Count > 0)
                {
                    item = q.Dequeue();
                    return true;
                }
            }
            return false;
        }

        public (int p0, int p1, int p2, int total) GetQueueDepths()
        {
            lock (_lock)
            {
                int p0 = _p0Queues.Values.Sum(q => q.Count);
                int p1 = _p1Queues.Values.Sum(q => q.Count);
                int p2 = _p2Queues.Values.Sum(q => q.Count);
                return (p0, p1, p2, p0 + p1 + p2);
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _p0Queues.Clear();
                _p0ClientOrder.Clear();
                _p0Cursor = 0;
                _p1Queues.Clear();
                _p1ClientOrder.Clear();
                _p1Cursor = 0;
                _p2Queues.Clear();
                _p2ClientOrder.Clear();
                _p2Cursor = 0;
            }
        }
    }
}
