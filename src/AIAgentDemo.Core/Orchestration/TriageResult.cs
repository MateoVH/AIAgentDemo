using System.ComponentModel;

namespace AIAgentDemo.Core.Orchestration;

public enum SupportIntent
{
    OrderStatus,
    RefundRequest,
    DamagedItem,
    MissingItem,
    Cancellation,
    ProductQuestion,
    Complaint,
    Other,
}

public enum Urgency
{
    Low,
    Medium,
    High,
}

public enum Sentiment
{
    Positive,
    Neutral,
    Negative,
}

/// <summary>Structured output of the Classifier agent (JSON schema generated from this type).</summary>
public sealed record TriageResult
{
    [Description("What the customer wants.")]
    public SupportIntent Intent { get; init; }

    [Description("ISO 639-1 code of the language the customer wrote in: \"es\" or \"en\".")]
    public string Language { get; init; } = "en";

    [Description("High when the customer is upset, lost money or the issue blocks them.")]
    public Urgency Urgency { get; init; }

    [Description("Overall tone of the message.")]
    public Sentiment Sentiment { get; init; }

    [Description("Order number mentioned by the customer (e.g. \"#1001\" -> 1001), or null.")]
    public int? OrderId { get; init; }

    [Description("Product name or keywords when the customer asks about a product, or null.")]
    public string? ProductQuery { get; init; }

    [Description("True when answering requires looking up customers, orders or products.")]
    public bool NeedsDataLookup { get; init; }

    [Description("One-sentence summary of the request, written in the operator language.")]
    public string Summary { get; init; } = "";
}
