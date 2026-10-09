namespace AIAgentDemo.Core.Data;

/// <summary>A pre-written customer message that exercises one scenario of the pipeline.</summary>
public sealed record SampleTicket(string Id, string Scenario, string CustomerName, string CustomerEmail, string Language, string Message);

/// <summary>Inbox seed: each ticket hits a different route (approval, no action, policy refusal...).</summary>
public static class SampleTickets
{
    public static IReadOnlyList<SampleTicket> All { get; } =
    [
        new("damaged", "refund", "Lucía Fernández", "lucia.fernandez@example.com", "es",
            "Hola, mi cafetera Brew Pro del pedido #1001 llegó con la jarra de vidrio rota. Estoy muy decepcionada, ¿me pueden devolver el dinero?"),
        new("late", "coupon", "Carlos Mendoza", "carlos.mendoza@example.com", "es",
            "Buenas tardes. Hice el pedido 1002 hace más de una semana y el smartwatch todavía no me llega. ¿Qué está pasando?"),
        new("cancel", "cancel", "James Wilson", "james.wilson@example.com", "en",
            "Hi, please cancel order 1004. I ordered the wrong chair model by mistake."),
        new("product", "info", "Sophie Martin", "sophie.martin@example.com", "en",
            "Hey! Is the Pulse Smartwatch S2 compatible with iPhone? And do you have it in stock right now?"),
        new("missing", "partial-refund", "Valentina Rojas", "valentina.rojas@example.com", "es",
            "Recibí mi pedido 1005 pero solo llegó el parlante, falta el mouse inalámbrico."),
        new("policy", "policy", "Emily Carter", "emily.carter@example.com", "en",
            "Hello, I'd like a refund for the headphones from order #1003. I just don't use them much."),
    ];
}
