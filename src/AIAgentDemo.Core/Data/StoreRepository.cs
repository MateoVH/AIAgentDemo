using System.Globalization;
using System.Text;
using AIAgentDemo.Core.Tools;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AIAgentDemo.Core.Data;

/// <summary>Read-side queries used by the Data Analyst tools and the UI.</summary>
public sealed class StoreRepository(StoreDatabase db, TimeProvider time)
{
    public const int MaxSqlRows = 50;

    // Catalog is in English; customers write in Spanish or English.
    private static readonly Dictionary<string, string> Synonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["audifonos"] = "headphones", ["auriculares"] = "headphones", ["cascos"] = "headphones",
        ["reloj"] = "smartwatch", ["teclado"] = "keyboard", ["cafetera"] = "coffee",
        ["lampara"] = "lamp", ["parlante"] = "speaker", ["altavoz"] = "speaker", ["bocina"] = "speaker",
        ["silla"] = "chair", ["raton"] = "mouse",
    };

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "you", "have", "del", "las", "los", "una", "uno", "con", "para", "que", "por", "tienen",
    };

    private string Now => time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public async Task<CustomerProfile?> GetCustomerProfileAsync(string email, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenReadOnlyAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<CustomerProfile>(new CommandDefinition(
            """
            SELECT c.id AS Id, c.name AS Name, c.email AS Email, c.country AS Country, c.language AS Language,
                   c.tier AS Tier, substr(c.created_at, 1, 10) AS CustomerSince,
                   (SELECT COUNT(*) FROM orders o WHERE o.customer_id = c.id) AS OrderCount,
                   (SELECT ROUND(IFNULL(SUM(o.total), 0.0), 2) FROM orders o
                     WHERE o.customer_id = c.id AND o.status <> 'cancelled') AS LifetimeValue
            FROM customers c
            WHERE lower(c.email) = lower(@email)
            """,
            new { email = email.Trim() },
            cancellationToken: cancellationToken));
    }

    public async Task<OrderDetails?> GetOrderDetailsAsync(int orderId, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenReadOnlyAsync(cancellationToken);
        var order = await connection.QuerySingleOrDefaultAsync<OrderDetails>(new CommandDefinition(
            """
            SELECT o.id AS OrderId, c.email AS CustomerEmail, c.name AS CustomerName, c.tier AS CustomerTier,
                   o.status AS Status, o.total AS Total,
                   (SELECT ROUND(IFNULL(SUM(r.amount), 0.0), 2) FROM refunds r WHERE r.order_id = o.id) AS RefundedAmount,
                   o.created_at AS CreatedAt, o.shipped_at AS ShippedAt, o.delivered_at AS DeliveredAt,
                   o.carrier AS Carrier, o.tracking_number AS TrackingNumber,
                   CAST(julianday(@now) - julianday(o.created_at) AS INTEGER) AS DaysSinceOrder,
                   CAST(julianday(@now) - julianday(o.shipped_at) AS INTEGER) AS DaysSinceShipment,
                   CAST(julianday(@now) - julianday(o.delivered_at) AS INTEGER) AS DaysSinceDelivery
            FROM orders o
            JOIN customers c ON c.id = o.customer_id
            WHERE o.id = @orderId
            """,
            new { orderId, now = Now },
            cancellationToken: cancellationToken));

        if (order is null)
        {
            return null;
        }

        var items = await connection.QueryAsync<OrderItemInfo>(new CommandDefinition(
            """
            SELECT p.sku AS Sku, p.name AS Product, i.quantity AS Quantity, i.unit_price AS UnitPrice
            FROM order_items i
            JOIN products p ON p.id = i.product_id
            WHERE i.order_id = @orderId
            ORDER BY p.name
            """,
            new { orderId },
            cancellationToken: cancellationToken));

        return order with { Items = items.ToList() };
    }

    public async Task<IReadOnlyList<OrderSummary>> ListCustomerOrdersAsync(string email, int limit = 5, CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenReadOnlyAsync(cancellationToken);
        var rows = await connection.QueryAsync<OrderSummary>(new CommandDefinition(
            """
            SELECT o.id AS OrderId, o.status AS Status, o.total AS Total, substr(o.created_at, 1, 10) AS CreatedAt,
                   (SELECT group_concat(p.name || ' x' || i.quantity, ', ')
                      FROM order_items i JOIN products p ON p.id = i.product_id
                     WHERE i.order_id = o.id) AS Items
            FROM orders o
            JOIN customers c ON c.id = o.customer_id
            WHERE lower(c.email) = lower(@email)
            ORDER BY o.created_at DESC
            LIMIT @limit
            """,
            new { email = email.Trim(), limit = Math.Clamp(limit, 1, 20) },
            cancellationToken: cancellationToken));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<ProductInfo>> SearchProductsAsync(string query, CancellationToken cancellationToken = default)
    {
        var terms = Tokenize(query);
        if (terms.Count == 0)
        {
            return [];
        }

        var sql = new StringBuilder(
            "SELECT sku AS Sku, name AS Name, category AS Category, price AS Price, stock AS Stock, description AS Description FROM products WHERE ");
        var parameters = new DynamicParameters();
        for (var i = 0; i < terms.Count; i++)
        {
            if (i > 0)
            {
                sql.Append(" OR ");
            }

            sql.Append(CultureInfo.InvariantCulture,
                $"lower(name) LIKE @t{i} OR lower(category) LIKE @t{i} OR lower(sku) LIKE @t{i} OR lower(description) LIKE @t{i}");
            parameters.Add($"t{i}", $"%{terms[i]}%");
        }

        sql.Append(" ORDER BY name LIMIT 10");

        await using var connection = await db.OpenReadOnlyAsync(cancellationToken);
        var rows = await connection.QueryAsync<ProductInfo>(new CommandDefinition(sql.ToString(), parameters, cancellationToken: cancellationToken));
        return rows.ToList();
    }

    /// <summary>
    /// Text-to-SQL escape hatch for the agent. Defense in depth: <see cref="SqlGuard"/> validates the
    /// statement, the connection is opened read-only, and results are capped at <see cref="MaxSqlRows"/>.
    /// </summary>
    public async Task<SqlQueryResult> RunReadOnlyQueryAsync(string sql, CancellationToken cancellationToken = default)
    {
        var validation = SqlGuard.Validate(sql);
        if (!validation.IsValid)
        {
            return new SqlQueryResult { Success = false, Error = validation.Error };
        }

        return await ExecuteReadOnlyAsync(validation.Sql, cancellationToken);
    }

    /// <summary>Executes SQL on the read-only connection without validation (exposed to tests to prove the second layer).</summary>
    internal async Task<SqlQueryResult> ExecuteReadOnlyAsync(string sql, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await db.OpenReadOnlyAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 5;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
            var rows = new List<Dictionary<string, object?>>();
            var truncated = false;
            while (await reader.ReadAsync(cancellationToken))
            {
                if (rows.Count == MaxSqlRows)
                {
                    truncated = true;
                    break;
                }

                var row = new Dictionary<string, object?>(columns.Count);
                for (var i = 0; i < columns.Count; i++)
                {
                    row[columns[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }

                rows.Add(row);
            }

            return new SqlQueryResult { Success = true, Columns = columns, Rows = rows, Truncated = truncated };
        }
        catch (SqliteException ex)
        {
            return new SqlQueryResult { Success = false, Error = ex.Message };
        }
    }

    public async Task<IReadOnlyList<CustomerListItem>> ListCustomersAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await db.OpenReadOnlyAsync(cancellationToken);
        var rows = await connection.QueryAsync<CustomerListItem>(new CommandDefinition(
            "SELECT name AS Name, email AS Email, language AS Language, tier AS Tier FROM customers ORDER BY name",
            cancellationToken: cancellationToken));
        return rows.ToList();
    }

    private static List<string> Tokenize(string query)
    {
        var words = TextNormalizer.RemoveDiacritics(query ?? string.Empty)
            .ToLowerInvariant()
            .Split([' ', ',', '.', '?', '!', ';', ':', '-', '/', '"', '\'', '(', ')'], StringSplitOptions.RemoveEmptyEntries);

        return words
            .Where(w => w.Length >= 3 && !StopWords.Contains(w))
            .Select(w => Synonyms.TryGetValue(w, out var mapped) ? mapped : w)
            .Distinct()
            .Take(5)
            .ToList();
    }
}
