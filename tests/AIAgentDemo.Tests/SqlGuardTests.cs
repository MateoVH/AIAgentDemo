using AIAgentDemo.Core.Tools;

namespace AIAgentDemo.Tests;

public class SqlGuardTests
{
    [Theory]
    [InlineData("SELECT * FROM orders")]
    [InlineData("select id, total from orders where status = 'shipped';")]
    [InlineData("WITH recent AS (SELECT * FROM orders) SELECT COUNT(*) FROM recent")]
    [InlineData("  SELECT c.name, SUM(o.total) FROM customers c JOIN orders o ON o.customer_id = c.id GROUP BY c.name")]
    public void Accepts_single_read_only_statements(string sql)
    {
        var result = SqlGuard.Validate(sql);

        Assert.True(result.IsValid, result.Error);
        Assert.DoesNotContain(';', result.Sql);
    }

    [Theory]
    [InlineData("DELETE FROM orders", "Only SELECT")]
    [InlineData("UPDATE orders SET total = 0", "Only SELECT")]
    [InlineData("SELECT * FROM orders; DROP TABLE orders", "single statement")]
    [InlineData("SELECT * FROM orders -- sneaky", "comments")]
    [InlineData("SELECT * FROM orders /* sneaky */", "comments")]
    [InlineData("WITH x AS (DELETE FROM refunds RETURNING *) SELECT * FROM x", "DELETE")]
    [InlineData("SELECT load_extension('evil')", "LOAD_EXTENSION")]
    [InlineData("PRAGMA table_info(orders)", "Only SELECT")]
    [InlineData("", "empty")]
    public void Rejects_anything_that_could_write_or_escape(string sql, string expectedError)
    {
        var result = SqlGuard.Validate(sql);

        Assert.False(result.IsValid);
        Assert.Contains(expectedError, result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_oversized_queries()
    {
        var result = SqlGuard.Validate("SELECT '" + new string('x', SqlGuard.MaxLength) + "'");

        Assert.False(result.IsValid);
    }
}
