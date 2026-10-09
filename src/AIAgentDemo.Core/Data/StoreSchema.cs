namespace AIAgentDemo.Core.Data;

internal static class StoreSchema
{
    /// <summary>Schema description shared with the Data Analyst agent so it can write correct SQL.</summary>
    public const string ForPrompt = """
        customers(id, name, email, country, language, tier['standard'|'gold'], created_at)
        products(id, sku, name, category, price, stock, description)
        orders(id, customer_id -> customers.id, status['pending'|'paid'|'shipped'|'delivered'|'cancelled'|'refunded'|'partially_refunded'], total, created_at, shipped_at, delivered_at, carrier, tracking_number)
        order_items(order_id -> orders.id, product_id -> products.id, quantity, unit_price)
        refunds(id, order_id, amount, reason, approved_by, run_id, created_at)
        coupons(code, customer_id, percent, reason, approved_by, run_id, expires_at, created_at)
        support_tickets(id, customer_id, order_id, priority, summary, run_id, created_at)
        Dates are UTC text 'YYYY-MM-DD HH:MM:SS'; money is USD.
        """;

    public const string DropAndCreate = """
        DROP TABLE IF EXISTS support_tickets;
        DROP TABLE IF EXISTS coupons;
        DROP TABLE IF EXISTS refunds;
        DROP TABLE IF EXISTS order_items;
        DROP TABLE IF EXISTS orders;
        DROP TABLE IF EXISTS products;
        DROP TABLE IF EXISTS customers;

        CREATE TABLE customers (
            id          INTEGER PRIMARY KEY,
            name        TEXT NOT NULL,
            email       TEXT NOT NULL UNIQUE,
            country     TEXT NOT NULL,
            language    TEXT NOT NULL,
            tier        TEXT NOT NULL,
            created_at  TEXT NOT NULL
        );

        CREATE TABLE products (
            id          INTEGER PRIMARY KEY,
            sku         TEXT NOT NULL UNIQUE,
            name        TEXT NOT NULL,
            category    TEXT NOT NULL,
            price       REAL NOT NULL,
            stock       INTEGER NOT NULL,
            description TEXT NOT NULL
        );

        CREATE TABLE orders (
            id              INTEGER PRIMARY KEY,
            customer_id     INTEGER NOT NULL REFERENCES customers(id),
            status          TEXT NOT NULL,
            total           REAL NOT NULL,
            created_at      TEXT NOT NULL,
            shipped_at      TEXT NULL,
            delivered_at    TEXT NULL,
            carrier         TEXT NULL,
            tracking_number TEXT NULL
        );

        CREATE TABLE order_items (
            order_id    INTEGER NOT NULL REFERENCES orders(id),
            product_id  INTEGER NOT NULL REFERENCES products(id),
            quantity    INTEGER NOT NULL,
            unit_price  REAL NOT NULL,
            PRIMARY KEY (order_id, product_id)
        );

        CREATE TABLE refunds (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            order_id    INTEGER NOT NULL REFERENCES orders(id),
            amount      REAL NOT NULL,
            reason      TEXT NOT NULL,
            approved_by TEXT NOT NULL,
            run_id      TEXT NOT NULL,
            created_at  TEXT NOT NULL
        );

        CREATE TABLE coupons (
            code        TEXT PRIMARY KEY,
            customer_id INTEGER NOT NULL REFERENCES customers(id),
            percent     INTEGER NOT NULL,
            reason      TEXT NOT NULL,
            approved_by TEXT NOT NULL,
            run_id      TEXT NOT NULL,
            expires_at  TEXT NOT NULL,
            created_at  TEXT NOT NULL
        );

        CREATE TABLE support_tickets (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            customer_id INTEGER NULL REFERENCES customers(id),
            order_id    INTEGER NULL REFERENCES orders(id),
            priority    TEXT NOT NULL,
            summary     TEXT NOT NULL,
            run_id      TEXT NOT NULL,
            created_at  TEXT NOT NULL
        );

        CREATE INDEX ix_orders_customer ON orders(customer_id);
        CREATE INDEX ix_refunds_order ON refunds(order_id);
        """;
}
