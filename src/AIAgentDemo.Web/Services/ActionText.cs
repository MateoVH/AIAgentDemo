using System.Text.Json;
using AIAgentDemo.Core;
using AIAgentDemo.Core.Tools;
using Microsoft.Extensions.Localization;

namespace AIAgentDemo.Web.Services;

/// <summary>Plain-language description of a proposed action, for the approval card and the actions list.</summary>
public static class ActionText
{
    public static string Describe(IStringLocalizer localizer, string tool, JsonElement arguments) => tool switch
    {
        ActionTools.IssueRefund => localizer["Action_issue_refund", Money.Usd(Decimal(arguments, "amount") ?? 0m), Text(arguments, "orderId")],
        ActionTools.CreateDiscountCoupon => localizer["Action_create_discount_coupon", Text(arguments, "percent"), Text(arguments, "customerEmail")],
        ActionTools.CancelOrder => localizer["Action_cancel_order", Text(arguments, "orderId")],
        ActionTools.EscalateToHuman => localizer["Action_escalate_to_human", Text(arguments, "priority")],
        _ => localizer["Action_unknown", tool],
    };

    public static string? Reason(JsonElement arguments) =>
        Text(arguments, "reason") is { Length: > 0 } reason ? reason : Text(arguments, "summary");

    private static decimal? Decimal(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.TryGetDecimal(out var number)
            ? number
            : null;

    private static string Text(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText()
            : "";
}
