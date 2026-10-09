using System.Data;
using System.Globalization;
using Dapper;

namespace AIAgentDemo.Core.Data;

/// <summary>
/// Seed data for the fictional "Nova Market" store. Dates are relative to "now" so the
/// business rules (30-day refund window, 7-day delivery SLA) behave the same on any day.
/// </summary>
internal static class StoreSeed
{
    public static async Task InsertAsync(IDbConnection connection, IDbTransaction transaction, DateTimeOffset now)
    {
        string Ago(double days) => now.AddDays(-days).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        await connection.ExecuteAsync(
            "INSERT INTO customers (id, name, email, country, language, tier, created_at) VALUES (@Id, @Name, @Email, @Country, @Language, @Tier, @CreatedAt)",
            new object[]
            {
                new { Id = 1, Name = "Lucía Fernández", Email = "lucia.fernandez@example.com", Country = "Colombia", Language = "es", Tier = "gold", CreatedAt = Ago(620) },
                new { Id = 2, Name = "Carlos Mendoza", Email = "carlos.mendoza@example.com", Country = "México", Language = "es", Tier = "standard", CreatedAt = Ago(210) },
                new { Id = 3, Name = "Emily Carter", Email = "emily.carter@example.com", Country = "United States", Language = "en", Tier = "standard", CreatedAt = Ago(400) },
                new { Id = 4, Name = "James Wilson", Email = "james.wilson@example.com", Country = "Canada", Language = "en", Tier = "gold", CreatedAt = Ago(880) },
                new { Id = 5, Name = "Valentina Rojas", Email = "valentina.rojas@example.com", Country = "Chile", Language = "es", Tier = "standard", CreatedAt = Ago(95) },
                new { Id = 6, Name = "Sophie Martin", Email = "sophie.martin@example.com", Country = "United Kingdom", Language = "en", Tier = "standard", CreatedAt = Ago(150) },
            },
            transaction);

        await connection.ExecuteAsync(
            "INSERT INTO products (id, sku, name, category, price, stock, description) VALUES (@Id, @Sku, @Name, @Category, @Price, @Stock, @Description)",
            new object[]
            {
                new { Id = 1, Sku = "NV-AUD-100", Name = "Aura ANC Wireless Headphones", Category = "Audio", Price = 129.90m, Stock = 34, Description = "Active noise cancelling, 40 h battery, USB-C, Bluetooth 5.3." },
                new { Id = 2, Sku = "NV-WEA-200", Name = "Pulse Smartwatch S2", Category = "Wearables", Price = 189.00m, Stock = 12, Description = "AMOLED display, GPS, 7-day battery. Compatible with iPhone (iOS 16+) and Android 10+." },
                new { Id = 3, Sku = "NV-ACC-300", Name = "Mecha Mechanical Keyboard", Category = "Accessories", Price = 89.90m, Stock = 0, Description = "Hot-swappable brown switches, RGB, Spanish and US layouts. Restock expected in 2 weeks." },
                new { Id = 4, Sku = "NV-HOM-400", Name = "Brew Pro Coffee Maker", Category = "Home", Price = 149.50m, Stock = 8, Description = "12-cup programmable coffee maker with glass carafe and warming plate." },
                new { Id = 5, Sku = "NV-HOM-410", Name = "Lumen LED Desk Lamp", Category = "Home", Price = 39.90m, Stock = 50, Description = "Dimmable LED lamp with 3 color temperatures and a USB charging port." },
                new { Id = 6, Sku = "NV-AUD-110", Name = "Boom Mini Bluetooth Speaker", Category = "Audio", Price = 59.90m, Stock = 21, Description = "Waterproof (IPX7) portable speaker, 12 h battery." },
                new { Id = 7, Sku = "NV-FUR-500", Name = "ErgoFlex Office Chair", Category = "Furniture", Price = 249.00m, Stock = 5, Description = "Ergonomic mesh chair with lumbar support and adjustable armrests." },
                new { Id = 8, Sku = "NV-ACC-310", Name = "Glide Wireless Mouse", Category = "Accessories", Price = 29.90m, Stock = 64, Description = "Silent clicks, 2.4 GHz + Bluetooth, 18-month battery life." },
            },
            transaction);

        // (order, customer, status, created, shipped, delivered, carrier, tracking, items[(product, qty, price)])
        var orders = new[]
        {
            Order(1001, 1, "delivered", Ago(6), Ago(5), Ago(2), "Servientrega", "SV-48213377", (4, 1, 149.50m)),
            Order(1002, 2, "shipped", Ago(11), Ago(9), null, "DHL Express", "DHL-7731902245", (2, 1, 189.00m)),
            Order(1003, 3, "delivered", Ago(50), Ago(48), Ago(45), "UPS", "1Z999AA10123456784", (1, 1, 129.90m)),
            Order(1004, 4, "paid", Ago(1), null, null, null, null, (7, 1, 249.00m)),
            Order(1005, 5, "delivered", Ago(14), Ago(12), Ago(10), "Chilexpress", "CX-55120987", (6, 1, 59.90m), (8, 1, 29.90m)),
            Order(1006, 6, "shipped", Ago(4), Ago(2), null, "Royal Mail", "RM-204417GB", (5, 1, 39.90m)),
            Order(1007, 1, "delivered", Ago(70), Ago(68), Ago(64), "Servientrega", "SV-40110254", (8, 1, 29.90m)),
            Order(1008, 4, "delivered", Ago(25), Ago(24), Ago(20), "Canada Post", "CP-7781120043", (3, 1, 89.90m)),
            Order(1009, 3, "delivered", Ago(15), Ago(14), Ago(12), "UPS", "1Z999AA10123459921", (5, 2, 39.90m)),
        };

        foreach (var order in orders)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO orders (id, customer_id, status, total, created_at, shipped_at, delivered_at, carrier, tracking_number)
                VALUES (@Id, @CustomerId, @Status, @Total, @CreatedAt, @ShippedAt, @DeliveredAt, @Carrier, @TrackingNumber)
                """,
                order.Row,
                transaction);

            await connection.ExecuteAsync(
                "INSERT INTO order_items (order_id, product_id, quantity, unit_price) VALUES (@OrderId, @ProductId, @Quantity, @UnitPrice)",
                order.Items,
                transaction);
        }
    }

    private static (object Row, object[] Items) Order(
        int id, int customerId, string status, string createdAt, string? shippedAt, string? deliveredAt,
        string? carrier, string? tracking, params (int ProductId, int Quantity, decimal UnitPrice)[] items)
    {
        var total = items.Sum(i => i.Quantity * i.UnitPrice);
        var row = new
        {
            Id = id, CustomerId = customerId, Status = status, Total = total, CreatedAt = createdAt,
            ShippedAt = shippedAt, DeliveredAt = deliveredAt, Carrier = carrier, TrackingNumber = tracking,
        };
        var itemRows = items
            .Select(i => (object)new { OrderId = id, i.ProductId, i.Quantity, i.UnitPrice })
            .ToArray();
        return (row, itemRows);
    }
}
