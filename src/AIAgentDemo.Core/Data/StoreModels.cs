namespace AIAgentDemo.Core.Data;

// Read models returned by the data tools. They are serialized to JSON and sent to the
// model, so property names double as the vocabulary the agents reason with.

public sealed record CustomerProfile
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string Email { get; init; } = "";
    public string Country { get; init; } = "";
    public string Language { get; init; } = "";
    public string Tier { get; init; } = "";
    public string CustomerSince { get; init; } = "";
    public int OrderCount { get; init; }
    public decimal LifetimeValue { get; init; }
}

public sealed record OrderItemInfo
{
    public string Sku { get; init; } = "";
    public string Product { get; init; } = "";
    public int Quantity { get; init; }
    public decimal UnitPrice { get; init; }
}

public sealed record OrderDetails
{
    public int OrderId { get; init; }
    public string CustomerEmail { get; init; } = "";
    public string CustomerName { get; init; } = "";
    public string CustomerTier { get; init; } = "";
    public string Status { get; init; } = "";
    public decimal Total { get; init; }
    public decimal RefundedAmount { get; init; }
    public string CreatedAt { get; init; } = "";
    public string? ShippedAt { get; init; }
    public string? DeliveredAt { get; init; }
    public string? Carrier { get; init; }
    public string? TrackingNumber { get; init; }
    public int DaysSinceOrder { get; init; }
    public int? DaysSinceShipment { get; init; }
    public int? DaysSinceDelivery { get; init; }
    public IReadOnlyList<OrderItemInfo> Items { get; init; } = [];
}

public sealed record OrderSummary
{
    public int OrderId { get; init; }
    public string Status { get; init; } = "";
    public decimal Total { get; init; }
    public string CreatedAt { get; init; } = "";
    public string Items { get; init; } = "";
}

public sealed record ProductInfo
{
    public string Sku { get; init; } = "";
    public string Name { get; init; } = "";
    public string Category { get; init; } = "";
    public decimal Price { get; init; }
    public int Stock { get; init; }
    public string Description { get; init; } = "";
}

public sealed record SqlQueryResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<string> Columns { get; init; } = [];
    public IReadOnlyList<Dictionary<string, object?>> Rows { get; init; } = [];
    public bool Truncated { get; init; }
}

/// <summary>Lightweight customer entry for the UI customer picker.</summary>
public sealed record CustomerListItem
{
    public string Name { get; init; } = "";
    public string Email { get; init; } = "";
    public string Language { get; init; } = "";
    public string Tier { get; init; } = "";
}
