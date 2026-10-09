using System.Globalization;
using System.Text;

namespace AIAgentDemo.Core.Tools;

internal static class TextNormalizer
{
    /// <summary>"Lámpara" → "Lampara". Lets keyword matching treat Spanish with or without accents alike.</summary>
    public static string RemoveDiacritics(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    public static string Fold(string text) => RemoveDiacritics(text).ToLowerInvariant();
}
