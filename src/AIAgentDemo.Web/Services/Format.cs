using System.Globalization;

namespace AIAgentDemo.Web.Services;

/// <summary>Display formatting that follows the UI culture (money stays in USD notation).</summary>
public static class Format
{
    public static string Duration(double? milliseconds) => milliseconds switch
    {
        null => "",
        < 1_000 => $"{milliseconds.Value.ToString("0", CultureInfo.CurrentCulture)} ms",
        < 60_000 => $"{(milliseconds.Value / 1_000).ToString("0.0", CultureInfo.CurrentCulture)} s",
        _ => $"{(int)(milliseconds.Value / 60_000)} min {(int)(milliseconds.Value / 1_000 % 60)} s",
    };

    public static string Number(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    public static int Words(string? text) =>
        string.IsNullOrWhiteSpace(text) ? 0 : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    public static string Language => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es" ? "es" : "en";
}
