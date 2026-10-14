using System.Globalization;
using Raskladushka.Core.Keys;

namespace Raskladushka.Core.Lang;

public static class LangData
{
    private static readonly Lazy<LangModel> Russian = new(() => LoadResource(Layout.Ru, "ru_50k.txt"));
    private static readonly Lazy<LangModel> English = new(() => LoadResource(Layout.En, "en_50k.txt"));

    public static IEnumerable<(string Word, long Count)> Parse(TextReader reader)
    {
        while (reader.ReadLine() is { } line)
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 2 && long.TryParse(fields[1], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var count))
                yield return (fields[0].ToLowerInvariant(), count);
        }
    }

    public static LangModel Load(Layout lang) => lang switch
    {
        Layout.Ru => Russian.Value,
        Layout.En => English.Value,
        _ => throw new ArgumentOutOfRangeException(nameof(lang))
    };

    private static LangModel LoadResource(Layout lang, string name)
    {
        using var stream = typeof(LangData).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("Embedded dictionary resource is missing.");
        using var reader = new StreamReader(stream);
        return LangModel.Build(lang, Parse(reader));
    }
}
