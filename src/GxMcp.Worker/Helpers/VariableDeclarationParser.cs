using System;
using System.Text.RegularExpressions;

namespace GxMcp.Worker.Helpers
{
    internal sealed class VariableDeclaration
    {
        internal VariableDeclaration(string name, string typeName, int length, int decimals, bool isCollection)
        {
            Name = name;
            TypeName = typeName;
            Length = length;
            Decimals = decimals;
            IsCollection = isCollection;
        }

        internal string Name { get; }
        internal string TypeName { get; }
        internal int Length { get; }
        internal int Decimals { get; }
        internal bool IsCollection { get; }
    }

    internal static class VariableDeclarationParser
    {
        private static readonly Regex DeclarationPattern = new Regex(
            @"&?(\w+)\s*:\s*([\w\.\-:]+(?:\s*,\s*[\w\.\-]+)?)(?:\s*\(\s*(\d+)(?:\s*,\s*(\d+))?\s*\))?(?:\s+(Collection))?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal static bool TryParse(string line, out VariableDeclaration declaration)
        {
            declaration = null;
            if (string.IsNullOrWhiteSpace(line)) return false;

            Match match = DeclarationPattern.Match(line);
            if (!match.Success) return false;

            declaration = new VariableDeclaration(
                match.Groups[1].Value,
                match.Groups[2].Value.Trim(),
                match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0,
                match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 0,
                match.Groups[5].Success);
            return true;
        }

        internal static bool TrySplitModuleQualifiedTypeName(string typeName, out string objectName, out string moduleName)
        {
            objectName = null;
            moduleName = null;
            if (string.IsNullOrWhiteSpace(typeName)) return false;

            int comma = typeName.IndexOf(',');
            if (comma <= 0 || comma != typeName.LastIndexOf(',')) return false;

            objectName = typeName.Substring(0, comma).Trim();
            moduleName = typeName.Substring(comma + 1).Trim();
            return objectName.Length > 0 && moduleName.Length > 0;
        }
    }
}
