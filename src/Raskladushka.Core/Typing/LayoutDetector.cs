using Raskladushka.Core.Keys;
using Raskladushka.Core.Lang;

namespace Raskladushka.Core.Typing;

public enum Verdict { Keep, Switch }

public enum DecisionReason { TooShort, Exception, Contraction, AltMoreFrequent, CurMoreFrequent, CurInDictionary, Trigram, NoEvidence }
public readonly record struct Decision(Verdict Verdict, DecisionReason Reason);

public sealed class LayoutDetector
{
    private static readonly string[] _contractionSuffixes = ["n't", "'s", "'re", "'ve", "'ll", "'d", "'m"];
    private readonly LangModel _en;
    private readonly LangModel _ru;
    private readonly Func<string, bool> _isException;
    private readonly Func<DetectorOptions> _options;
    private DetectorOptions? _cachedOptions;
    private double _enWeak;
    private double _enStrong;
    private double _ruWeak;
    private double _ruStrong;

    public LayoutDetector(LangModel en, LangModel ru, Func<string, bool> isException, Func<DetectorOptions> options)
    {
        _en = en;
        _ru = ru;
        _isException = isException;
        _options = options;
    }

    public Verdict Decide(IReadOnlyList<KeyStroke> word, Layout current) => Explain(word, current).Verdict;

    public Decision Explain(IReadOnlyList<KeyStroke> word, Layout current)
    {
        var other = KeyMap.Other(current);
        var (cur, curLead, curTrail) = SplitEdges(KeyMap.Render(word, current), current);
        var (alt, altLead, altTrail) = SplitEdges(KeyMap.Render(word, other), other);
        var options = _options();
        if (alt.Count(c => IsLetter(c, other)) < options.MinWordLength)
            return new(Verdict.Keep, DecisionReason.TooShort);
        if (_isException(cur))
            return new(Verdict.Keep, DecisionReason.Exception);

        var currentModel = current == Layout.En ? _en : _ru;
        var otherModel = other == Layout.En ? _en : _ru;
        if (current == Layout.En && IsContraction(cur))
            return new(Verdict.Keep, DecisionReason.Contraction);
        if (otherModel.Contains(alt))
        {
            var altWeight = otherModel.Frequency(alt) * EdgePenalty(altLead, altTrail, options);
            var curWeight = currentModel.Contains(cur)
                ? currentModel.Frequency(cur) * EdgePenalty(curLead, curTrail, options)
                : 0;
            return altWeight >= options.FrequencyRatio * curWeight
                ? new(Verdict.Switch, DecisionReason.AltMoreFrequent)
                : new(Verdict.Keep, DecisionReason.CurMoreFrequent);
        }
        if (currentModel.Contains(cur))
            return new(Verdict.Keep, DecisionReason.CurInDictionary);

        if (_cachedOptions != options)
        {
            _enWeak = _en.Percentile(options.WeakPercentile);
            _enStrong = _en.Percentile(options.StrongPercentile);
            _ruWeak = _ru.Percentile(options.WeakPercentile);
            _ruStrong = _ru.Percentile(options.StrongPercentile);
            _cachedOptions = options;
        }
        var weak = current == Layout.En ? _enWeak : _ruWeak;
        var strong = other == Layout.En ? _enStrong : _ruStrong;
        if (currentModel.Score(cur) < weak && otherModel.Score(alt) >= strong)
            return new(Verdict.Switch, DecisionReason.Trigram);
        return new(Verdict.Keep, DecisionReason.NoEvidence);
    }

    public static string TrimToLetters(string text, Layout layout) => SplitEdges(text, layout).Core;

    private static (string Core, string Lead, string Trail) SplitEdges(string text, Layout layout)
    {
        var lower = text.ToLowerInvariant();
        var start = 0;
        var end = lower.Length;
        while (start < end && !IsLetter(lower[start], layout))
            start++;
        while (end > start && !IsLetter(lower[end - 1], layout))
            end--;
        return (lower[start..end], lower[..start], lower[end..]);
    }

    private static double EdgePenalty(string lead, string trail, DetectorOptions options)
    {
        var penalty = 1.0;
        foreach (var c in lead)
            penalty *= options.RareEdgePenalty;
        foreach (var c in trail)
            penalty *= c is '.' or ',' ? options.CommonEdgePenalty : options.RareEdgePenalty;
        return penalty;
    }

    private bool IsContraction(string word)
    {
        foreach (var suffix in _contractionSuffixes)
        {
            if (!word.EndsWith(suffix, StringComparison.Ordinal))
                continue;
            var stem = word[..^suffix.Length];
            if (_en.Contains(stem) || (suffix == "n't" && _en.Contains(stem + "n")))
                return true;
        }
        return false;
    }

    private static bool IsLetter(char c, Layout layout) => layout switch
    {
        Layout.En => c is >= 'a' and <= 'z',
        Layout.Ru => c is >= 'а' and <= 'я' or 'ё',
        _ => false
    };
}
