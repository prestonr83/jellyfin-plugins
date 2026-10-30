using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>Materializes grouped media IDs once instead of repeatedly comparing a misestimated semi-join.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only fixed membership syntax is inserted into a structurally matched native SELECT. Original SQL and bound parameters are preserved; no user values are interpolated.")]
internal sealed partial class PostgresGroupedItemIdsInterceptor : DbCommandInterceptor
{
    private static readonly ConditionalWeakTable<DbCommand, string> RewrittenReads = new();

    /// <inheritdoc />
    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        RewriteCommand(command);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        RewriteCommand(command);
        return ValueTask.FromResult(result);
    }

    internal static string Rewrite(string sql)
    {
        if (!sql.TrimStart().StartsWith("SELECT", StringComparison.Ordinal)
            || !sql.Contains("COALESCE(min(", StringComparison.Ordinal)
            || sql.Contains("__jf_grouped_ids_", StringComparison.Ordinal))
        {
            return sql;
        }

        var matches = GroupedIds().Matches(sql);
        if (matches.Count == 0)
        {
            return sql;
        }

        var parentheses = FindParentheses(sql);
        var edits = new List<(int Start, int Length, string Text)>();
        var definitions = new List<string>();
        for (var index = matches.Count - 1; index >= 0; index--)
        {
            var match = matches[index];
            var opening = match.Groups["opening"].Index;
            if (!parentheses.TryGetValue(opening, out var closing))
            {
                continue;
            }

            var body = sql.AsSpan(opening + 1, closing - opening - 1).TrimEnd();
            if (!body.EndsWith($"GROUP BY {match.Groups["alias"].Value}.\"PresentationUniqueKey\"", StringComparison.Ordinal))
            {
                continue;
            }

            var source = body.ToString();
            // A nested grouped set or an outer alias requires separate scoping
            // analysis. Leave those uncommon shapes to the original query.
            if (GroupedIds().IsMatch(source) || !IsSelfContained(source))
            {
                return sql;
            }

            var projectionEnd = match.Groups["projection"].Index + match.Groups["projection"].Length - opening - 1;
            source = source.Insert(projectionEnd, " AS \"Id\"");
            var name = "\"__jf_grouped_ids_" + definitions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\"";
            definitions.Add(name + " AS MATERIALIZED (" + source + ")");
            edits.Add((opening + 1, closing - opening - 1, "SELECT \"Id\" FROM " + name));
        }

        edits.Sort(static (left, right) => right.Start.CompareTo(left.Start));
        foreach (var edit in edits)
        {
            sql = sql.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Text);
        }

        return definitions.Count == 0 ? sql : "WITH " + string.Join(", ", definitions) + " " + sql;
    }

    internal static bool IsRewrittenRead(DbCommand command)
        => RewrittenReads.TryGetValue(command, out var sql) && string.Equals(command.CommandText, sql, StringComparison.Ordinal);

    private static void RewriteCommand(DbCommand command)
    {
        var original = command.CommandText;
        var rewritten = Rewrite(original);
        if (!ReferenceEquals(original, rewritten))
        {
            command.CommandText = rewritten;
            RewrittenReads.Remove(command);
            RewrittenReads.Add(command, rewritten);
        }
    }

    private static bool IsSelfContained(string source)
    {
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in DeclaredAliases().Matches(source))
        {
            aliases.Add(match.Groups["alias"].Value);
        }

        foreach (Match match in ReferencedAliases().Matches(source))
        {
            if (!aliases.Contains(match.Groups["alias"].Value))
            {
                return false;
            }
        }

        return true;
    }

    private static Dictionary<int, int> FindParentheses(string sql)
    {
        var pairs = new Dictionary<int, int>();
        var stack = new Stack<int>();
        for (var index = 0; index < sql.Length; index++)
        {
            var character = sql[index];
            if (character is '\'' or '"')
            {
                var escape = character == '\'' && index > 0 && sql[index - 1] is 'e' or 'E';
                var closed = false;
                while (++index < sql.Length)
                {
                    if (escape && sql[index] == '\\')
                    {
                        index++;
                    }
                    else if (sql[index] == character)
                    {
                        if (index + 1 < sql.Length && sql[index + 1] == character)
                        {
                            index++;
                        }
                        else
                        {
                            closed = true;
                            break;
                        }
                    }
                }

                if (!closed)
                {
                    return [];
                }
            }
            else if (character == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
            {
                while (index + 1 < sql.Length && sql[index + 1] != '\n')
                {
                    index++;
                }
            }
            else if (character == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {
                var depth = 1;
                index += 2;
                for (; index < sql.Length - 1 && depth > 0; index++)
                {
                    if (sql[index] == '/' && sql[index + 1] == '*')
                    {
                        depth++;
                        index++;
                    }
                    else if (sql[index] == '*' && sql[index + 1] == '/')
                    {
                        depth--;
                        index++;
                    }
                }

                if (depth > 0)
                {
                    return [];
                }

                index--;
            }
            else if (character == '$' && (index + 1 < sql.Length) && (sql[index + 1] == '$' || char.IsLetter(sql[index + 1]) || sql[index + 1] == '_'))
            {
                // Dollar-quoted SQL is not emitted in this native query shape.
                return [];
            }
            else if (character == '(')
            {
                stack.Push(index);
            }
            else if (character == ')')
            {
                if (!stack.TryPop(out var opening))
                {
                    return [];
                }

                pairs.Add(opening, index);
            }
            else if (character == ';' && index < sql.TrimEnd().Length - 1)
            {
                return [];
            }
        }

        return stack.Count == 0 ? pairs : [];
    }

    [GeneratedRegex("""\."Id"\s+IN\s*(?<opening>\()\s*SELECT\s+(?<projection>COALESCE\(min\((?<alias>[A-Za-z_][A-Za-z0-9_]*)\."Id"\) FILTER \(WHERE \k<alias>\."PrimaryVersionId" IS NULL\), min\(\k<alias>\."Id"\)\))\s+FROM "BaseItems" AS \k<alias>\s+WHERE""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 100)]
    private static partial Regex GroupedIds();

    [GeneratedRegex("""\b(?:FROM|JOIN)\s+"[A-Za-z_][A-Za-z0-9_]*"\s+(?:AS\s+)?(?<alias>[A-Za-z_][A-Za-z0-9_]*)""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 100)]
    private static partial Regex DeclaredAliases();

    [GeneratedRegex("""\b(?<alias>[A-Za-z_][A-Za-z0-9_]*)\.""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 100)]
    private static partial Regex ReferencedAliases();
}
