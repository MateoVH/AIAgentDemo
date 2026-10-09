using System.Text.RegularExpressions;

namespace AIAgentDemo.Core.Tools;

public readonly record struct SqlGuardResult(bool IsValid, string Sql, string? Error);

/// <summary>
/// First line of defense for the agent's free-form SQL tool: a single SELECT/WITH statement,
/// no comments and no write/DDL/PRAGMA keywords. The second line is the read-only connection.
/// </summary>
public static partial class SqlGuard
{
    public const int MaxLength = 2000;

    public static SqlGuardResult Validate(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return Fail("The query is empty.");
        }

        var statement = sql.Trim();
        if (statement.Length > MaxLength)
        {
            return Fail($"The query exceeds {MaxLength} characters.");
        }

        if (statement.Contains("--", StringComparison.Ordinal) || statement.Contains("/*", StringComparison.Ordinal))
        {
            return Fail("SQL comments are not allowed.");
        }

        if (statement.EndsWith(';'))
        {
            statement = statement[..^1].TrimEnd();
        }

        if (statement.Contains(';'))
        {
            return Fail("Only a single statement is allowed.");
        }

        if (!LeadingKeyword().IsMatch(statement))
        {
            return Fail("Only SELECT queries (optionally starting with WITH) are allowed.");
        }

        var forbidden = ForbiddenKeyword().Match(statement);
        if (forbidden.Success)
        {
            return Fail($"Keyword '{forbidden.Value.ToUpperInvariant()}' is not allowed in a read-only query.");
        }

        return new SqlGuardResult(true, statement, null);
    }

    private static SqlGuardResult Fail(string error) => new(false, string.Empty, error);

    [GeneratedRegex(@"^\s*(select|with)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingKeyword();

    [GeneratedRegex(
        @"\b(insert|update|delete|drop|alter|create|replace|upsert|merge|attach|detach|pragma|vacuum|reindex|analyze|truncate|grant|revoke|begin|commit|rollback|savepoint|release|load_extension|readfile|writefile)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ForbiddenKeyword();
}
