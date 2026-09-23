using System;
using System.Collections.Generic;

namespace GxMcp.Worker.Services
{
    internal static class CallSiteMatcher
    {
        private static readonly HashSet<string> InvocationMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Call", "Udp", "Submit", "Link", "Execute", "Popup", "CallOptions", "Load"
        };

        private static readonly HashSet<string> LegacyInvocationNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Call", "Link", "Submit"
        };

        internal static bool Matches(ParsedCall call, string canonicalName, string moduleQualifiedName)
        {
            if (string.IsNullOrWhiteSpace(canonicalName)) return false;
            string candidate = GetCandidateIdentifier(call);
            if (string.IsNullOrWhiteSpace(candidate) || candidate[0] == '&') return false;

            if (string.Equals(candidate, canonicalName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate, moduleQualifiedName, StringComparison.OrdinalIgnoreCase))
                return true;

            int dot = candidate.LastIndexOf('.');
            string unqualified = dot >= 0 ? candidate.Substring(dot + 1) : candidate;
            return string.Equals(unqualified, canonicalName, StringComparison.OrdinalIgnoreCase);
        }

        internal static string GetCandidateObjectName(ParsedCall call)
        {
            string candidate = GetCandidateIdentifier(call);
            if (string.IsNullOrWhiteSpace(candidate)) return null;
            int dot = candidate.LastIndexOf('.');
            return dot >= 0 ? candidate.Substring(dot + 1) : candidate;
        }

        private static string GetCandidateIdentifier(ParsedCall call)
        {
            if (call == null || string.IsNullOrWhiteSpace(call.Callee)) return null;

            string callee = call.Callee.Trim();
            if (callee.IndexOf('.') < 0 && LegacyInvocationNames.Contains(callee))
            {
                if (call.Args == null || call.Args.Count == 0) return null;
                return Unquote(call.Args[0]);
            }

            int dot = callee.LastIndexOf('.');
            string member = dot >= 0 ? callee.Substring(dot + 1) : callee;
            if (dot > 0 && InvocationMethods.Contains(member))
                callee = callee.Substring(0, dot);

            return callee.Trim();
        }

        private static string Unquote(string value)
        {
            if (value == null) return null;
            string candidate = value.Trim();
            if (candidate.Length >= 2
                && (candidate[0] == '\'' || candidate[0] == '"')
                && candidate[candidate.Length - 1] == candidate[0])
                candidate = candidate.Substring(1, candidate.Length - 2).Trim();
            return candidate;
        }
    }
}
