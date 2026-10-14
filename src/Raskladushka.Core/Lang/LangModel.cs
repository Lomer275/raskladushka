using Raskladushka.Core.Keys;

namespace Raskladushka.Core.Lang;

public sealed class LangModel
{
    private readonly Dictionary<string, double> frequencies;
    private readonly int alphabetSize;
    private readonly int stride;
    private readonly double[] logProbabilities;
    private readonly double[] scores;

    private LangModel(Layout lang, Dictionary<string, double> counts, int alphabetSize, double[] logProbabilities)
    {
        Lang = lang;
        var total = counts.Values.Sum();
        frequencies = counts.ToDictionary(entry => entry.Key, entry => entry.Value / total, StringComparer.Ordinal);
        this.alphabetSize = alphabetSize;
        stride = alphabetSize + 2;
        this.logProbabilities = logProbabilities;
        scores = frequencies.Keys.Select(Score).Order().ToArray();
    }

    public Layout Lang { get; }
    public int WordCount => frequencies.Count;
    public IEnumerable<(string Word, double Frequency)> Words =>
        frequencies.Select(entry => (entry.Key, entry.Value));

    public double Frequency(string lowerWord) => frequencies.GetValueOrDefault(lowerWord);

    public static LangModel Build(Layout lang, IEnumerable<(string Word, long Count)> entries)
    {
        var alphabetSize = lang switch
        {
            Layout.En => 26,
            Layout.Ru => 33,
            _ => throw new ArgumentOutOfRangeException(nameof(lang))
        };
        var counts = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (word, count) in entries)
        {
            var lowerWord = word.ToLowerInvariant();
            if (!IsAlphabetic(lowerWord, lang))
                continue;
            counts.TryGetValue(lowerWord, out var previous);
            counts[lowerWord] = previous + count;
        }

        var stride = alphabetSize + 2;
        var pairs = new double[stride * stride];
        var triples = new double[stride * stride * stride];
        foreach (var (word, count) in counts)
        {
            var weight = Math.Log10(count + 10);
            var a = alphabetSize + 1;
            var b = a;
            for (var i = 0; i <= word.Length; i++)
            {
                var c = i == word.Length ? alphabetSize : LetterIndex(word[i], lang);
                var pair = a * stride + b;
                pairs[pair] += weight;
                triples[pair * stride + c] += weight;
                a = b;
                b = c;
            }
        }

        for (var pair = 0; pair < pairs.Length; pair++)
        {
            var denominator = pairs[pair] + 0.1 * (alphabetSize + 1);
            for (var c = 0; c <= alphabetSize; c++)
            {
                var index = pair * stride + c;
                triples[index] = Math.Log10((triples[index] + 0.1) / denominator);
            }
        }

        return new LangModel(lang, counts, alphabetSize, triples);
    }

    public bool Contains(string lowerWord) => frequencies.ContainsKey(lowerWord);

    public double Score(string lowerWord)
    {
        if (!IsAlphabetic(lowerWord, Lang))
            return double.NegativeInfinity;

        var a = alphabetSize + 1;
        var b = a;
        var sum = 0.0;
        for (var i = 0; i <= lowerWord.Length; i++)
        {
            var c = i == lowerWord.Length ? alphabetSize : LetterIndex(lowerWord[i], Lang);
            sum += logProbabilities[(a * stride + b) * stride + c];
            a = b;
            b = c;
        }
        return sum / (lowerWord.Length + 1);
    }

    public double Percentile(double p)
    {
        if (double.IsNaN(p) || p < 0 || p > 100)
            throw new ArgumentOutOfRangeException(nameof(p));
        if (scores.Length == 0)
            return double.NegativeInfinity;
        var index = Math.Max(0, (int)Math.Ceiling(p / 100 * scores.Length) - 1);
        return scores[index];
    }

    public static bool IsAlphabetic(string lowerWord, Layout lang)
    {
        if (lowerWord.Length == 0)
            return false;
        foreach (var c in lowerWord)
        {
            if (LetterIndex(c, lang) < 0)
                return false;
        }
        return true;
    }

    private static int LetterIndex(char c, Layout lang) => lang switch
    {
        Layout.En when c is >= 'a' and <= 'z' => c - 'a',
        Layout.Ru when c is >= 'а' and <= 'я' => c - 'а',
        Layout.Ru when c == 'ё' => 32,
        _ => -1
    };
}
