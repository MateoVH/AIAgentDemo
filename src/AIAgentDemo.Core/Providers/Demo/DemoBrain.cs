using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIAgentDemo.Core.Data;
using AIAgentDemo.Core.Orchestration;
using AIAgentDemo.Core.Tools;
using Microsoft.Extensions.AI;

namespace AIAgentDemo.Core.Providers.Demo;

/// <summary>
/// Deterministic rules standing in for model judgment in Demo mode. They follow the same
/// policies the real agents receive in their instructions, so both modes behave alike.
/// </summary>
internal static partial class DemoBrain
{
    public static ChatMessage Classify(DemoConversation conversation) =>
        new(ChatRole.Assistant, AgentJson.Serialize(DemoTriage.Classify(conversation.CustomerMessage, conversation.Context.OperatorLanguage)));

    public static ChatMessage Investigate(DemoConversation conversation)
    {
        var triage = conversation.Context.Triage ?? new TriageResult();
        var email = conversation.Context.CustomerEmail;

        if (conversation.ToolResults.Count == 0)
        {
            var calls = new List<AIContent> { Call("get_customer_profile", ("email", email)) };
            if (triage.OrderId is int orderId)
            {
                calls.Add(Call("get_order_details", ("orderId", orderId)));
            }
            else if (IsOrderRelated(triage.Intent))
            {
                calls.Add(Call("list_customer_orders", ("email", email), ("limit", 3)));
            }

            if (!string.IsNullOrWhiteSpace(triage.ProductQuery))
            {
                calls.Add(Call("search_products", ("query", triage.ProductQuery)));
            }

            return new ChatMessage(ChatRole.Assistant, calls);
        }

        // Second hop: the order number was unknown, so inspect the most recent order found.
        if (conversation.Result("get_order_details") is null
            && conversation.Result("list_customer_orders")?.As<List<OrderSummary>>() is [var latest, ..])
        {
            return new ChatMessage(ChatRole.Assistant, [Call("get_order_details", ("orderId", latest.OrderId))]);
        }

        return new ChatMessage(ChatRole.Assistant, DemoFacts.Summarize(conversation));
    }

    public static ChatMessage Decide(DemoConversation conversation)
    {
        if (conversation.ToolResults.Count > 0)
        {
            return new ChatMessage(ChatRole.Assistant, DemoDecision.Summarize(conversation));
        }

        var (rationale, calls) = DemoDecision.Plan(conversation);
        var contents = new List<AIContent> { new TextContent(rationale) };
        contents.AddRange(calls);
        return new ChatMessage(ChatRole.Assistant, contents);
    }

    public static ChatMessage Write(DemoConversation conversation) =>
        new(ChatRole.Assistant, DemoReply.Compose(conversation.Context, conversation.CustomerMessage));

    internal static bool IsOrderRelated(SupportIntent intent) => intent is
        SupportIntent.OrderStatus or SupportIntent.RefundRequest or SupportIntent.DamagedItem or
        SupportIntent.MissingItem or SupportIntent.Cancellation or SupportIntent.Complaint;

    internal static FunctionCallContent Call(string name, params (string Key, object? Value)[] arguments) =>
        new("call_" + Guid.NewGuid().ToString("N")[..12], name, arguments.ToDictionary(a => a.Key, a => a.Value));

    internal static T? Evidence<T>(CaseContext context, string tool)
        where T : class
    {
        var evidence = context.Evidence?.LastOrDefault(e => e.Tool == tool);
        if (evidence is null || evidence.Result.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
        {
            return null;
        }

        if (evidence.Result.ValueKind == JsonValueKind.Object
            && evidence.Result.TryGetProperty("found", out var found) && found.ValueKind == JsonValueKind.False)
        {
            return null;
        }

        return evidence.Result.Deserialize<T>(AgentJson.Options);
    }

    internal static decimal? Decimal(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.TryGetDecimal(out var number)
            ? number
            : null;

    internal static int? Int(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? number
            : null;

    internal static string Plural(int count, string singular, string plural) =>
        $"{count} {(count == 1 ? singular : plural)}";

    internal static string Days(int? days, bool spanish) =>
        spanish ? Plural(days ?? 0, "día", "días") : Plural(days ?? 0, "day", "days");

    internal static string Status(string status, bool spanish) => !spanish ? status.Replace('_', ' ') : status switch
    {
        "pending" => "pendiente",
        "paid" => "pagado",
        "shipped" => "enviado",
        "delivered" => "entregado",
        "cancelled" => "cancelado",
        "refunded" => "reembolsado",
        "partially_refunded" => "reembolsado parcialmente",
        _ => status,
    };

    /// <summary>English catalog keyword → how customers may refer to it (folded, no accents).</summary>
    internal static readonly Dictionary<string, string[]> ProductWords = new()
    {
        ["headphones"] = ["headphone", "audifono", "auricular", "cascos"],
        ["smartwatch"] = ["smartwatch", "reloj", "watch"],
        ["keyboard"] = ["keyboard", "teclado"],
        ["coffee"] = ["coffee", "cafetera"],
        ["lamp"] = ["lamp", "lampara"],
        ["speaker"] = ["speaker", "parlante", "altavoz", "bocina"],
        ["chair"] = ["chair", "silla"],
        ["mouse"] = ["mouse", "raton"],
    };
}

internal static partial class DemoTriage
{
    public static TriageResult Classify(string message, string operatorLanguage)
    {
        var text = TextNormalizer.Fold(message);
        var intent = Intents.FirstOrDefault(i => i.Cue.IsMatch(text)).Intent;
        var negative = NegativeCue().IsMatch(text);
        var sentiment = negative || intent is SupportIntent.DamagedItem or SupportIntent.MissingItem or SupportIntent.Complaint
            ? Sentiment.Negative
            : PositiveCue().IsMatch(text) ? Sentiment.Positive : Sentiment.Neutral;
        var urgency = UrgentCue().IsMatch(text) || (negative && intent != SupportIntent.ProductQuestion)
            ? Urgency.High
            : intent is SupportIntent.ProductQuestion or SupportIntent.Other ? Urgency.Low : Urgency.Medium;

        int? orderId = OrderNumber().Match(message) is { Success: true } match
            ? int.Parse(match.Value, CultureInfo.InvariantCulture)
            : null;
        var product = intent == SupportIntent.ProductQuestion
            ? DemoBrain.ProductWords.FirstOrDefault(p => p.Value.Any(text.Contains)).Key
            : null;

        return new TriageResult
        {
            Intent = intent,
            Language = DetectLanguage(message, text),
            Urgency = urgency,
            Sentiment = sentiment,
            OrderId = orderId,
            ProductQuery = product,
            NeedsDataLookup = intent != SupportIntent.Other,
            Summary = Summarize(intent, orderId, product, operatorLanguage.StartsWith("es", StringComparison.OrdinalIgnoreCase)),
        };
    }

    // Checked in order: the first matching cue wins (a broken item that the customer wants refunded is a damage case).
    private static readonly (SupportIntent Intent, Regex Cue)[] Intents =
    [
        (SupportIntent.DamagedItem, Cue(@"\b(rot[oa]s?|danad|quebrad|averiad|defectuos|no funciona|no prende|broken|damaged|cracked|defective|shattered|not working|doesn'?t work|does not work)")),
        (SupportIntent.MissingItem, Cue(@"\b(falta|faltan|falto|solo llego|no venia|no vino|missing|only (got|received)|did ?n[o']?t include|not in the box)")),
        (SupportIntent.Cancellation, Cue(@"\bcancel")),
        (SupportIntent.RefundRequest, Cue(@"\b(reembols|devoluci|devolver|devuelv|refund|money back|return)")),
        (SupportIntent.OrderStatus, Cue(@"\b(donde esta|no (me )?ha llegado|no (me )?llega|todavia no|aun no|where is|has ?n[o']?t arrived|not arrived|still waiting|tracking|rastreo|seguimiento|estado de mi pedido|status of my order|retras|late\b|delayed)")),
        (SupportIntent.ProductQuestion, Cue(@"\b(stock|disponib|compatib|precio|cuanto cuesta|price|how much|available|tienen|do you (have|sell)|especificac|specs|bateria|battery|garantia|warranty)")),
        (SupportIntent.Complaint, Cue(@"\b(pesim|terrible|horrible|queja|reclamo|complain|worst|unacceptable|inaceptable)")),
        (SupportIntent.Other, Cue(@".")),
    ];

    private static Regex Cue(string pattern) => new(pattern, RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> SpanishWords =
    [
        "el", "la", "los", "las", "mi", "mis", "de", "del", "que", "por", "para", "con", "pero", "hola", "buenas", "gracias",
        "pedido", "llego", "todavia", "esta", "estoy", "quiero", "pueden", "necesito", "muy", "una", "solo", "falta", "hice",
        "hace", "como", "cuando", "donde", "tienen", "favor", "me", "y", "es", "no",
    ];

    private static readonly HashSet<string> EnglishWords =
    [
        "the", "my", "is", "are", "i", "you", "please", "order", "hi", "hello", "hey", "thanks", "thank", "do", "does", "have",
        "with", "and", "from", "just", "it", "was", "for", "what", "where", "when", "can", "would", "like", "it's", "i'd", "don't",
    ];

    private static string DetectLanguage(string original, string folded)
    {
        var words = Regex.Split(folded, @"[^a-z']+").Where(w => w.Length > 0).ToList();
        var spanish = words.Count(SpanishWords.Contains) + (original.IndexOfAny(['¿', '¡', 'ñ', 'á', 'é', 'í', 'ó', 'ú']) >= 0 ? 3 : 0);
        var english = words.Count(EnglishWords.Contains);
        return spanish > english ? "es" : "en";
    }

    private static string Summarize(SupportIntent intent, int? orderId, string? product, bool spanish)
    {
        var order = orderId is null ? "" : $" #{orderId}";
        return (intent, spanish) switch
        {
            (SupportIntent.OrderStatus, true) => $"Consulta por el estado del pedido{order}, que aún no ha llegado.",
            (SupportIntent.OrderStatus, false) => $"Asks about the status of order{order}, which has not arrived yet.",
            (SupportIntent.DamagedItem, true) => $"Reporta un producto dañado en el pedido{order} y pide una solución.",
            (SupportIntent.DamagedItem, false) => $"Reports a damaged item in order{order} and asks for a solution.",
            (SupportIntent.MissingItem, true) => $"Reporta que falta un artículo en el pedido{order}.",
            (SupportIntent.MissingItem, false) => $"Reports a missing item in order{order}.",
            (SupportIntent.Cancellation, true) => $"Solicita cancelar el pedido{order}.",
            (SupportIntent.Cancellation, false) => $"Asks to cancel order{order}.",
            (SupportIntent.RefundRequest, true) => $"Solicita un reembolso del pedido{order}.",
            (SupportIntent.RefundRequest, false) => $"Requests a refund for order{order}.",
            (SupportIntent.ProductQuestion, true) => $"Pregunta por precio, stock o características de un producto ({product ?? "catálogo"}).",
            (SupportIntent.ProductQuestion, false) => $"Asks about price, stock or features of a product ({product ?? "catalog"}).",
            (SupportIntent.Complaint, true) => "Presenta una queja sobre el servicio.",
            (SupportIntent.Complaint, false) => "Files a complaint about the service.",
            (_, true) => "Consulta general.",
            _ => "General inquiry.",
        };
    }

    [GeneratedRegex(@"\b(decepcion|molest|enojad|frustr|furios|pesim|terrible|horrible|disappoint|angry|upset|annoyed|unacceptable|worst|ridicul|que esta pasando|what'?s going on)", RegexOptions.CultureInvariant)]
    private static partial Regex NegativeCue();

    [GeneratedRegex(@"\b(gracias|thanks|thank you|great|genial|excelente|love|encanta|awesome)", RegexOptions.CultureInvariant)]
    private static partial Regex PositiveCue();

    [GeneratedRegex(@"\b(urgente|urgent|asap|inmediat|immediately|ya mismo)", RegexOptions.CultureInvariant)]
    private static partial Regex UrgentCue();

    [GeneratedRegex(@"(?<!\d)\d{4,6}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex OrderNumber();
}

internal static class DemoFacts
{
    public static string Summarize(DemoConversation conversation)
    {
        var es = conversation.IsSpanish;
        var email = conversation.Context.CustomerEmail;
        var lines = new List<string>();

        foreach (var result in conversation.ToolResults)
        {
            switch (result.Tool)
            {
                case "get_customer_profile" when result.As<CustomerProfile>() is { } p:
                    lines.Add(es
                        ? $"Cliente: {p.Name} (nivel {p.Tier}), {p.Country}; {DemoBrain.Plural(p.OrderCount, "pedido", "pedidos")}, valor histórico {Money.Usd(p.LifetimeValue)}."
                        : $"Customer: {p.Name} ({p.Tier} tier), {p.Country}; {DemoBrain.Plural(p.OrderCount, "order", "orders")}, lifetime value {Money.Usd(p.LifetimeValue)}.");
                    break;
                case "get_customer_profile":
                    lines.Add(es ? $"No existe un cliente con el correo {email}." : $"There is no customer with email {email}.");
                    break;
                case "get_order_details" when result.As<OrderDetails>() is { } o:
                    lines.AddRange(DescribeOrder(o, email, es));
                    break;
                case "get_order_details":
                    var missing = DemoBrain.Int(result.Arguments, "orderId");
                    lines.Add(es ? $"El pedido #{missing} no existe." : $"Order #{missing} does not exist.");
                    break;
                case "list_customer_orders" when result.As<List<OrderSummary>>() is { Count: > 0 } orders:
                    var listed = string.Join(", ", orders.Select(x => $"#{x.OrderId} ({DemoBrain.Status(x.Status, es)}, {Money.Usd(x.Total)})"));
                    lines.Add(es ? $"Pedidos recientes: {listed}." : $"Recent orders: {listed}.");
                    break;
                case "search_products" when result.As<List<ProductInfo>>() is { Count: > 0 } products:
                    lines.AddRange(products.Take(3).Select(x => es
                        ? $"Producto {x.Name} ({x.Sku}): {Money.Usd(x.Price)}, stock {x.Stock} unidades. {x.Description}"
                        : $"Product {x.Name} ({x.Sku}): {Money.Usd(x.Price)}, {x.Stock} units in stock. {x.Description}"));
                    break;
                case "search_products":
                    lines.Add(es ? "No hay productos que coincidan con la búsqueda." : "No products match the search.");
                    break;
            }
        }

        return string.Join('\n', lines.Select(l => "- " + l));
    }

    private static IEnumerable<string> DescribeOrder(OrderDetails o, string caseEmail, bool es)
    {
        var refunded = o.RefundedAmount > 0 ? (es ? $", reembolsado {Money.Usd(o.RefundedAmount)}" : $", refunded {Money.Usd(o.RefundedAmount)}") : "";
        yield return es
            ? $"Pedido #{o.OrderId}: {DemoBrain.Status(o.Status, true)}, total {Money.Usd(o.Total)}{refunded}; creado hace {DemoBrain.Days(o.DaysSinceOrder, true)}."
            : $"Order #{o.OrderId}: {DemoBrain.Status(o.Status, false)}, total {Money.Usd(o.Total)}{refunded}; placed {DemoBrain.Days(o.DaysSinceOrder, false)} ago.";

        if (o.DaysSinceShipment is int shipped)
        {
            var delivery = o.DaysSinceDelivery is int delivered
                ? (es ? $"entregado hace {DemoBrain.Days(delivered, true)}" : $"delivered {DemoBrain.Days(delivered, false)} ago")
                : (es ? "aún sin entregar" : "not delivered yet");
            yield return es
                ? $"Enviado hace {DemoBrain.Days(shipped, true)} con {o.Carrier} (guía {o.TrackingNumber}); {delivery}."
                : $"Shipped {DemoBrain.Days(shipped, false)} ago with {o.Carrier} (tracking {o.TrackingNumber}); {delivery}.";
        }

        var items = string.Join(", ", o.Items.Select(i => $"{i.Product} x{i.Quantity} ({Money.Usd(i.UnitPrice)})"));
        yield return es ? $"Artículos: {items}." : $"Items: {items}.";

        if (!string.Equals(o.CustomerEmail, caseEmail, StringComparison.OrdinalIgnoreCase))
        {
            yield return es ? $"ATENCIÓN: el pedido pertenece a otro cliente ({o.CustomerEmail})." : $"WARNING: the order belongs to another customer ({o.CustomerEmail}).";
        }
        else if (o.Status == "shipped" && o.DaysSinceShipment > 7)
        {
            yield return es ? "Supera el plazo de entrega estándar de 7 días." : "It is past the standard 7-day delivery window.";
        }
        else if (o.DaysSinceDelivery > 30)
        {
            yield return es ? "La entrega supera la ventana de devoluciones de 30 días." : "Delivery is outside the 30-day return window.";
        }
    }
}

internal static class DemoDecision
{
    public static (string Rationale, IReadOnlyList<FunctionCallContent> Calls) Plan(DemoConversation conversation)
    {
        var context = conversation.Context;
        var es = conversation.IsSpanish;
        var triage = context.Triage ?? new TriageResult();
        var order = DemoBrain.Evidence<OrderDetails>(context, "get_order_details");
        var profile = DemoBrain.Evidence<CustomerProfile>(context, "get_customer_profile");
        var ownOrder = order is not null && string.Equals(order.CustomerEmail, context.CustomerEmail, StringComparison.OrdinalIgnoreCase);

        if (DemoBrain.IsOrderRelated(triage.Intent) && triage.Intent != SupportIntent.Complaint && !ownOrder)
        {
            return (es
                ? "Sin acción: no hay un pedido verificado de este cliente; se le pedirá el número de pedido (política: solo actuar sobre pedidos propios)."
                : "No action: there is no verified order for this customer, so we will ask for the order number (policy: only act on the customer's own orders).", []);
        }

        switch (triage.Intent)
        {
            case SupportIntent.DamagedItem or SupportIntent.MissingItem:
            {
                if (order!.Status is not ("delivered" or "partially_refunded") || order.DaysSinceDelivery > 30)
                {
                    return (es
                        ? $"Sin acción: el pedido #{order.OrderId} no es elegible para reembolso ({DemoBrain.Status(order.Status, true)}, entregado hace {DemoBrain.Days(order.DaysSinceDelivery, true)}; política 1)."
                        : $"No action: order #{order.OrderId} is not eligible for a refund ({order.Status}, delivered {DemoBrain.Days(order.DaysSinceDelivery, false)} ago; policy 1).", []);
                }

                var remaining = order.Total - order.RefundedAmount;
                var item = triage.Intent == SupportIntent.MissingItem ? MissingItem(order, conversation.CustomerMessage) : null;
                var amount = item is null ? remaining : Math.Min(item.UnitPrice * item.Quantity, remaining);
                var reason = (item, es) switch
                {
                    (null, true) => "El producto llegó dañado",
                    (null, false) => "Item arrived damaged",
                    (_, true) => $"Falta el artículo: {item.Product}",
                    _ => $"Missing item: {item.Product}",
                };
                var rationale = (es, item) switch
                {
                    (true, null) => $"Producto dañado reportado {DemoBrain.Days(order.DaysSinceDelivery, true)} después de la entrega (dentro de la ventana de 30 días): corresponde reembolsar {Money.Usd(amount)} según la política 1.",
                    (true, _) => $"Falta {item.Product} en un pedido entregado hace {DemoBrain.Days(order.DaysSinceDelivery, true)}: corresponde reembolsar solo ese artículo ({Money.Usd(amount)}) según la política 1.",
                    (false, null) => $"Damaged item reported {DemoBrain.Days(order.DaysSinceDelivery, false)} after delivery (within the 30-day window): policy 1 calls for a {Money.Usd(amount)} refund.",
                    _ => $"{item.Product} is missing from an order delivered {DemoBrain.Days(order.DaysSinceDelivery, false)} ago: policy 1 calls for refunding that item only ({Money.Usd(amount)}).",
                };
                return (rationale, [DemoBrain.Call(ActionTools.IssueRefund, ("orderId", order.OrderId), ("amount", amount), ("reason", reason))]);
            }

            case SupportIntent.RefundRequest:
            {
                if (order!.Status == "delivered" && order.DaysSinceDelivery <= 30)
                {
                    var amount = order.Total - order.RefundedAmount;
                    return (es
                        ? $"Reembolso solicitado {DemoBrain.Days(order.DaysSinceDelivery, true)} después de la entrega, dentro de la ventana de 30 días (política 2)."
                        : $"Refund requested {DemoBrain.Days(order.DaysSinceDelivery, false)} after delivery, within the 30-day window (policy 2).",
                        [DemoBrain.Call(ActionTools.IssueRefund, ("orderId", order.OrderId), ("amount", amount), ("reason", es ? "Cambio de opinión dentro de los 30 días" : "Change of mind within 30 days"))]);
                }

                if (triage.Urgency == Urgency.High)
                {
                    return (es ? "Fuera de política, pero el cliente está muy molesto: se escala a un especialista." : "Outside policy, but the customer is very upset: escalating to a specialist.",
                        [DemoBrain.Call(ActionTools.EscalateToHuman, ("orderId", order.OrderId), ("priority", "high"), ("summary", triage.Summary))]);
                }

                return (es
                    ? $"Sin acción: el pedido #{order.OrderId} se entregó hace {DemoBrain.Days(order.DaysSinceDelivery, true)}, fuera de la ventana de 30 días, y no hay defecto reportado (política 2). Se explicará la política al cliente."
                    : $"No action: order #{order.OrderId} was delivered {DemoBrain.Days(order.DaysSinceDelivery, false)} ago, outside the 30-day window, and no defect was reported (policy 2). The reply will explain the policy.", []);
            }

            case SupportIntent.OrderStatus:
            {
                if (order!.Status == "shipped" && order.DaysSinceShipment > 7)
                {
                    var gold = string.Equals(profile?.Tier ?? order.CustomerTier, "gold", StringComparison.OrdinalIgnoreCase);
                    var percent = gold ? 15 : 10;
                    return (es
                        ? $"El pedido #{order.OrderId} lleva {DemoBrain.Days(order.DaysSinceShipment, true)} en tránsito (plazo de 7 días): corresponde un cupón de disculpa del {percent}% (política 3). No se reembolsa un pedido en tránsito."
                        : $"Order #{order.OrderId} has been in transit for {DemoBrain.Days(order.DaysSinceShipment, false)} (7-day window): policy 3 calls for a {percent}% apology coupon. No refund while in transit.",
                        [DemoBrain.Call(ActionTools.CreateDiscountCoupon, ("customerEmail", context.CustomerEmail), ("percent", percent), ("reason", es ? $"Entrega retrasada del pedido #{order.OrderId}" : $"Delayed delivery of order #{order.OrderId}"))]);
                }

                return (es
                    ? $"Sin acción: el pedido #{order.OrderId} está {DemoBrain.Status(order.Status, true)} y dentro del plazo; solo se informa el estado."
                    : $"No action: order #{order.OrderId} is {order.Status} and within the delivery window; we only share the status.", []);
            }

            case SupportIntent.Cancellation:
            {
                if (order!.Status is "pending" or "paid")
                {
                    return (es
                        ? $"El pedido #{order.OrderId} está {DemoBrain.Status(order.Status, true)} y aún no se envía: se puede cancelar (política 4)."
                        : $"Order #{order.OrderId} is {order.Status} and has not shipped yet: it can be cancelled (policy 4).",
                        [DemoBrain.Call(ActionTools.CancelOrder, ("orderId", order.OrderId), ("reason", es ? "Solicitud del cliente" : "Customer request"))]);
                }

                return (es
                    ? $"Sin acción: el pedido #{order.OrderId} ya fue {DemoBrain.Status(order.Status, true)} y no se puede cancelar (política 4)."
                    : $"No action: order #{order.OrderId} is already {order.Status} and cannot be cancelled (policy 4).", []);
            }

            case SupportIntent.Complaint:
                return (es ? "Queja que requiere seguimiento: se escala a un especialista (política 5)." : "Complaint that needs follow-up: escalating to a specialist (policy 5).",
                    [DemoBrain.Call(ActionTools.EscalateToHuman, ("orderId", order?.OrderId), ("priority", triage.Urgency == Urgency.High ? "high" : "normal"), ("summary", triage.Summary))]);

            default:
                return (es ? "Sin acción: consulta informativa (política 6)." : "No action: informational request (policy 6).", []);
        }
    }

    /// <summary>Final note after the tools ran: the earlier rationale plus what actually happened.</summary>
    public static string Summarize(DemoConversation conversation)
    {
        var es = conversation.IsSpanish;
        var outcomes = conversation.ToolResults.Select(result =>
        {
            if (result.Json is not { ValueKind: JsonValueKind.Object } json)
            {
                // The function-invocation loop reports a rejected approval as a plain-text result.
                return es
                    ? $"El revisor humano rechazó {result.Tool}: no se ejecutó y el caso queda para revisión manual."
                    : $"The human reviewer rejected {result.Tool}: it was not executed and the case stays open for manual review.";
            }

            var success = json.TryGetProperty("success", out var flag) && flag.ValueKind == JsonValueKind.True;
            var reference = json.TryGetProperty("reference", out var r) ? r.GetString() : null;
            var message = json.TryGetProperty("message", out var m) ? m.GetString() : null;
            var orderId = DemoBrain.Int(result.Arguments, "orderId");
            var amount = Money.Usd(DemoBrain.Decimal(result.Arguments, "amount") ?? 0m);
            var percent = DemoBrain.Int(result.Arguments, "percent");

            if (!success)
            {
                return es ? $"Las reglas de negocio bloquearon {result.Tool}: {message}" : $"Business rules blocked {result.Tool}: {message}";
            }

            return (result.Tool, es) switch
            {
                (ActionTools.IssueRefund, true) => $"Reembolso de {amount} ejecutado para el pedido #{orderId} (ref. {reference}).",
                (ActionTools.IssueRefund, false) => $"Refund of {amount} executed for order #{orderId} (ref. {reference}).",
                (ActionTools.CreateDiscountCoupon, true) => $"Cupón {reference} del {percent}% creado para el cliente.",
                (ActionTools.CreateDiscountCoupon, false) => $"Coupon {reference} for {percent}% off created for the customer.",
                (ActionTools.CancelOrder, true) => $"Pedido #{orderId} cancelado; se devolverá el pago completo.",
                (ActionTools.CancelOrder, false) => $"Order #{orderId} cancelled; the full payment will be returned.",
                (ActionTools.EscalateToHuman, true) => $"Caso escalado a un especialista (ticket {reference}).",
                (ActionTools.EscalateToHuman, false) => $"Case escalated to a specialist (ticket {reference}).",
                (_, true) => $"{result.Tool} ejecutado: {message}",
                _ => $"{result.Tool} executed: {message}",
            };
        });

        return string.Join(' ', new[] { conversation.AssistantText }.Concat(outcomes).Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    /// <summary>The order item named right after a "missing" cue ("falta el mouse", "missing the mouse").</summary>
    private static OrderItemInfo? MissingItem(OrderDetails order, string message)
    {
        var text = TextNormalizer.Fold(message);
        var cue = Regex.Match(text, @"\b(falta|faltan|falto|missing|no vino|no venia|did ?n[o']?t include)");
        var from = cue.Success ? cue.Index : 0;

        return order.Items
            .Select(item =>
            {
                var name = TextNormalizer.Fold(item.Product);
                var words = DemoBrain.ProductWords
                    .Where(p => name.Contains(p.Key, StringComparison.Ordinal))
                    .SelectMany(p => p.Value)
                    .Append(name.Split(' ')[0]);
                var positions = words.Select(w => text.IndexOf(w, from, StringComparison.Ordinal)).Where(i => i >= 0).ToList();
                return (Item: item, Position: positions.Count > 0 ? positions.Min() : int.MaxValue);
            })
            .Where(x => x.Position != int.MaxValue)
            .OrderBy(x => x.Position)
            .Select(x => x.Item)
            .FirstOrDefault();
    }
}

internal static class DemoReply
{
    public static string Compose(CaseContext context, string customerMessage)
    {
        var es = (context.CustomerLanguage ?? context.Triage?.Language ?? "en").StartsWith("es", StringComparison.OrdinalIgnoreCase);
        var triage = context.Triage ?? new TriageResult();
        var profile = DemoBrain.Evidence<CustomerProfile>(context, "get_customer_profile");
        var order = DemoBrain.Evidence<OrderDetails>(context, "get_order_details");
        var products = DemoBrain.Evidence<List<ProductInfo>>(context, "search_products");
        var actions = context.Actions ?? [];

        ActionOutcome? Executed(string tool) => actions.LastOrDefault(a => a.Tool == tool && a.Status == ActionStatus.Executed);
        var refund = Executed(ActionTools.IssueRefund);
        var coupon = Executed(ActionTools.CreateDiscountCoupon);
        var cancel = Executed(ActionTools.CancelOrder);
        var ticket = Executed(ActionTools.EscalateToHuman);
        var blocked = actions.Any(a => a.Status is ActionStatus.Rejected or ActionStatus.Failed);

        var body = new List<string>();
        var id = order?.OrderId ?? triage.OrderId;
        var specialist = es
            ? $"Lamentamos lo ocurrido con tu pedido{Hash(id)}. Un especialista de nuestro equipo revisará tu caso personalmente y te contactará en las próximas 24 horas."
            : $"We're sorry about the trouble with your order{Hash(id)}. A specialist from our team will review your case personally and get back to you within 24 hours.";

        switch (triage.Intent)
        {
            case SupportIntent.DamagedItem or SupportIntent.MissingItem or SupportIntent.RefundRequest when refund is not null:
                var amount = Money.Usd(DemoBrain.Decimal(refund.Arguments, "amount") ?? 0m);
                var product = order?.Items.FirstOrDefault()?.Product;
                body.Add((triage.Intent, es) switch
                {
                    (SupportIntent.DamagedItem, true) => $"Lamentamos mucho que tu {product} del pedido #{id} haya llegado en mal estado. Ya aprobamos un reembolso de {amount} a tu método de pago original; lo verás reflejado en 5 a 10 días hábiles (referencia {refund.Reference}).",
                    (SupportIntent.DamagedItem, false) => $"We're very sorry your {product} from order #{id} arrived damaged. We've approved a refund of {amount} to your original payment method; you'll see it within 5–10 business days (reference {refund.Reference}).",
                    (SupportIntent.MissingItem, true) => $"Sentimos mucho que tu pedido #{id} haya llegado incompleto. Ya aprobamos un reembolso de {amount} por el artículo faltante; lo verás reflejado en tu método de pago en 5 a 10 días hábiles (referencia {refund.Reference}).",
                    (SupportIntent.MissingItem, false) => $"We're sorry your order #{id} arrived incomplete. We've approved a refund of {amount} for the missing item; you'll see it on your payment method within 5–10 business days (reference {refund.Reference}).",
                    (_, true) => $"Procesamos el reembolso de {amount} de tu pedido #{id} a tu método de pago original; lo verás reflejado en 5 a 10 días hábiles (referencia {refund.Reference}).",
                    _ => $"We've processed a refund of {amount} for order #{id} to your original payment method; you'll see it within 5–10 business days (reference {refund.Reference}).",
                });
                break;

            case SupportIntent.RefundRequest when !blocked && order is { DaysSinceDelivery: > 30 }:
                body.Add(es
                    ? $"Revisamos tu pedido #{id}: fue entregado hace {DemoBrain.Days(order.DaysSinceDelivery, true)}, fuera de nuestra ventana de devoluciones de 30 días, por lo que no podemos procesar un reembolso. Si el producto presenta alguna falla, con gusto te ayudamos con la garantía del fabricante."
                    : $"We checked order #{id}: it was delivered {DemoBrain.Days(order.DaysSinceDelivery, false)} ago, which is outside our 30-day return window, so we're unable to issue a refund. If the product has a defect, we'd be glad to help you with a warranty claim.");
                break;

            case SupportIntent.OrderStatus when order is { Status: "shipped" }:
                body.Add(es
                    ? $"Tu pedido #{id} salió hace {DemoBrain.Days(order.DaysSinceShipment, true)} con {order.Carrier} (guía {order.TrackingNumber})."
                    : $"Your order #{id} shipped {DemoBrain.Days(order.DaysSinceShipment, false)} ago with {order.Carrier} (tracking number {order.TrackingNumber}).");
                if (coupon is not null)
                {
                    var percent = DemoBrain.Int(coupon.Arguments, "percent");
                    body.Add(es
                        ? $"Sabemos que está tardando más de lo esperado y lo sentimos. Como disculpa, te regalamos el cupón {coupon.Reference} con {percent}% de descuento para tu próxima compra, válido por 90 días."
                        : $"We know it's taking longer than expected, and we're sorry. As an apology, here is coupon {coupon.Reference} for {percent}% off your next purchase, valid for 90 days.");
                }
                else
                {
                    body.Add(order.DaysSinceShipment > 7
                        ? (es ? "Sabemos que está tardando más de lo esperado y lo sentimos mucho; puedes seguir el envío en línea con tu número de guía." : "We know it's taking longer than expected and we're very sorry; you can follow the shipment online with your tracking number.")
                        : (es ? "Va en camino y debería llegar en los próximos días." : "It's on its way and should arrive in the next few days."));
                }

                break;

            case SupportIntent.OrderStatus when order is { Status: "delivered" }:
                body.Add(es
                    ? $"Según nuestro sistema, tu pedido #{id} fue entregado hace {DemoBrain.Days(order.DaysSinceDelivery, true)}. Si no lo recibiste, respóndenos y lo revisamos de inmediato."
                    : $"Our records show order #{id} was delivered {DemoBrain.Days(order.DaysSinceDelivery, false)} ago. If you didn't receive it, reply to this message and we'll look into it right away.");
                break;

            case SupportIntent.Cancellation when cancel is not null:
                body.Add(es
                    ? $"Tu pedido #{id} fue cancelado correctamente. El valor total de {Money.Usd(order?.Total ?? 0)} será devuelto a tu método de pago original en 5 a 10 días hábiles."
                    : $"Your order #{id} has been cancelled. The full amount of {Money.Usd(order?.Total ?? 0)} will be returned to your original payment method within 5–10 business days.");
                break;

            case SupportIntent.Cancellation when !blocked && order is { Status: "shipped" or "delivered" }:
                body.Add(es
                    ? $"Tu pedido #{id} ya fue enviado, por lo que no podemos cancelarlo. Cuando lo recibas, puedes solicitar la devolución dentro de los 30 días siguientes a la entrega."
                    : $"Your order #{id} has already shipped, so we can't cancel it. Once it arrives, you can request a return within 30 days of delivery.");
                break;

            case SupportIntent.ProductQuestion when products is { Count: > 0 }:
                var p = products[0];
                var stock = p.Stock > 0
                    ? (es ? $"tenemos {p.Stock} unidades disponibles" : $"we have {p.Stock} units in stock")
                    : (es ? "en este momento está agotado" : "it's currently out of stock");
                body.Add(es
                    ? $"¡Gracias por tu interés! El {p.Name} cuesta {Money.Usd(p.Price)} y {stock}. Según la ficha técnica: \"{p.Description}\""
                    : $"Thanks for your interest! The {p.Name} costs {Money.Usd(p.Price)} and {stock}. {p.Description}");
                break;

            case SupportIntent.ProductQuestion:
                body.Add(es
                    ? "No encontramos ese producto en nuestro catálogo. ¿Podrías darnos más detalles para ayudarte?"
                    : "We couldn't find that product in our catalog. Could you share a few more details so we can help?");
                break;

            case SupportIntent.Complaint when ticket is null:
                body.Add(es
                    ? "Lamentamos mucho tu experiencia. Tomamos nota de tus comentarios y un especialista revisará tu caso."
                    : "We're very sorry about your experience. We've noted your feedback and a specialist will review your case.");
                break;

            case SupportIntent.Other:
                body.Add(es
                    ? "Gracias por escribirnos. Revisamos tu mensaje y un miembro de nuestro equipo te responderá pronto."
                    : "Thanks for reaching out. We've read your message and a member of our team will get back to you shortly.");
                break;

            default:
                body.Add(order is null && ticket is null
                    ? (es ? "Para ayudarte mejor, ¿podrías indicarnos el número de pedido?" : "To help you faster, could you share your order number?")
                    : specialist);
                break;
        }

        if (ticket is not null)
        {
            body.Add(es
                ? $"Escalamos tu caso a un especialista (ticket {ticket.Reference}), quien te contactará en las próximas 24 horas."
                : $"We've escalated your case to a specialist (ticket {ticket.Reference}), who will contact you within 24 hours.");
        }

        var firstName = profile?.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var greeting = es
            ? (firstName is null ? "Hola," : $"Hola, {firstName}:")
            : (firstName is null ? "Hi there," : $"Hi {firstName},");
        var closing = es
            ? "Quedamos atentos a cualquier otra consulta.\n\nUn saludo,\nEquipo de Soporte de Nova Market"
            : "Let us know if there's anything else we can help with.\n\nBest regards,\nNova Market Support Team";

        return $"{greeting}\n\n{string.Join("\n\n", body)}\n\n{closing}";
    }

    private static string Hash(int? id) => id is null ? "" : $" #{id}";
}
