using System.Text;
using System.Text.RegularExpressions;

namespace Raskladushka.Core.Dictation;

public static class TranscriptCleaner
{
    private static readonly HashSet<string> _hallucinations = new(new[]
    {
        "Продолжение следует", "Субтитры сделал DimaTorzok", "Субтитры создавал DimaTorzok",
        "Субтитры делал DimaTorzok", "Редактор субтитров А.Семкин Корректор А.Егорова",
        "Спасибо за просмотр", "Thanks for watching", "Подписывайтесь на канал"
    }.Select(Normalize));

    public static string? Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var result = new StringBuilder();
        foreach (var segment in raw.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (result.Length > 0)
                result.Append(char.IsLetterOrDigit(result[^1]) && char.IsUpper(segment[0]) ? ". " : " ");
            result.Append(segment);
        }

        var text = Regex.Replace(result.ToString(), @"\s+", " ").Trim();
        return text.Length == 0 || _hallucinations.Contains(Normalize(text)) ? null : text + " ";
    }

    private static string Normalize(string text) =>
        new(text.Where(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c)).Select(char.ToLowerInvariant).ToArray());
}
