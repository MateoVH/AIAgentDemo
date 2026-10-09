using System.Globalization;

namespace AIAgentDemo.Core;

/// <summary>Culture-independent money formatting (store prices and LLM costs are USD).</summary>
public static class Money
{
    public static string Usd(decimal amount) => amount.ToString("$#,##0.00", CultureInfo.InvariantCulture);

    /// <summary>Budget limits: "$0.50", or "$0.002" when the limit is below a cent.</summary>
    public static string Budget(decimal amount) =>
        amount >= 0.01m ? Usd(amount) : amount.ToString("$0.######", CultureInfo.InvariantCulture);

    /// <summary>LLM costs are often fractions of a cent, so they get more decimals.</summary>
    public static string Cost(decimal amount) => amount switch
    {
        0m => "$0.00",
        < 0.01m => amount.ToString("$0.00000", CultureInfo.InvariantCulture),
        < 1m => amount.ToString("$0.0000", CultureInfo.InvariantCulture),
        _ => amount.ToString("$#,##0.00", CultureInfo.InvariantCulture),
    };
}
