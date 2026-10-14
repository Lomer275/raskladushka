using System.Globalization;
using System.Text;
using Raskladushka.Core.Keys;
using Raskladushka.Core.Lang;
using Raskladushka.Core.Typing;

namespace Raskladushka.Core.Tests;

public class QualityTests
{
    [Fact]
    [Trait("Category", "Quality")]
    public void DetectorMeetsQualityTargets()
    {
        var detector = new LayoutDetector(LangData.Load(Layout.En), LangData.Load(Layout.Ru),
            _ => false, () => new DetectorOptions());
        var ruWords = LangData.Load(Layout.Ru).Words.ToArray();
        var enWords = LangData.Load(Layout.En).Words.ToArray();
        var period = Measure("M5-en-знак", enWords, Layout.En, Layout.En, false, true, 0.0001, PhysKey.Period);
        var comma = Measure("M5-en-знак", enWords, Layout.En, Layout.En, false, true, 0.0001, PhysKey.Comma);
        var measures = new[]
        {
            Measure("M1", ruWords.Where(w => w.Word.Length >= 3), Layout.Ru, Layout.En, true, false, 0.97),
            Measure("M1-вес", ruWords.Where(w => w.Word.Length >= 3), Layout.Ru, Layout.En, true, true, 0.99),
            Measure("M2", enWords.Where(w => w.Word.Length >= 3), Layout.En, Layout.Ru, true, false, 0.97),
            Measure("M2-вес", enWords.Where(w => w.Word.Length >= 3), Layout.En, Layout.Ru, true, true, 0.985),
            Measure("M3-ru", TestWords("ru_rare.txt", Layout.Ru), Layout.Ru, Layout.Ru, false, false, 0.01),
            Measure("M3-en", TestWords("en_rare.txt", Layout.En), Layout.En, Layout.En, false, false, 0.01),
            Measure("M4", TestWords("tech_words.txt", Layout.En), Layout.En, Layout.En, false, false, 0.01),
            Measure("M5-ru", ruWords, Layout.Ru, Layout.Ru, false, true, 0.0001),
            Measure("M5-en", enWords, Layout.En, Layout.En, false, true, 0.0001),
            period.Ratio >= comma.Ratio ? period : comma
        };

        var report = new StringBuilder("# Качество определения раскладки\n\n");
        report.AppendLine("| Мера | Числитель | Знаменатель | Доля | Цель | Итог |");
        report.AppendLine("|---|---:|---:|---:|---|---|");
        foreach (var measure in measures)
            report.AppendLine($"| {measure.Name} | {measure.Switches.ToString("G17", CultureInfo.InvariantCulture)} | {measure.Total.ToString("G17", CultureInfo.InvariantCulture)} | {measure.Ratio.ToString("P4", CultureInfo.InvariantCulture)} | {(measure.Detection ? "≥ " : "≤ ") + measure.Target.ToString("P4", CultureInfo.InvariantCulture)} | {(measure.Passed ? "Пройдена" : "Не пройдена")} |");
        report.AppendLine($"\nM5-en-знак: выбран {(period.Ratio >= comma.Ratio ? "Period" : "Comma")}; Period — {period.Ratio.ToString("P6", CultureInfo.InvariantCulture)}, Comma — {comma.Ratio.ToString("P6", CultureInfo.InvariantCulture)}.");
        foreach (var measure in measures)
        {
            report.AppendLine($"\n## {measure.Name}: примеры промахов\n");
            report.AppendLine("Исходное слово → альтернативное прочтение; решение.\n");
            if (measure.Misses.Count == 0)
                report.AppendLine("Промахов нет.");
            foreach (var miss in measure.Misses)
                report.AppendLine($"- {miss}");
        }

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "Raskladushka.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        var artifacts = Path.Combine(root.FullName, "artifacts");
        Directory.CreateDirectory(artifacts);
        File.WriteAllText(Path.Combine(artifacts, "quality.md"), report.ToString());
        Assert.True(measures.All(m => m.Passed), report.ToString());

        QualityMeasure Measure(string name, IEnumerable<(string Word, double Frequency)> words, Layout source, Layout current, bool detection,
            bool weighted, double target, PhysKey? trailing = null)
        {
            var total = 0.0;
            var switches = 0.0;
            var misses = new List<string>();
            foreach (var (word, frequency) in words.OrderByDescending(entry => entry.Frequency))
            {
                var strokes = new List<KeyStroke>();
                foreach (var c in word)
                {
                    if (!KeyMap.TryFromChar(c, source, out var stroke))
                        break;
                    strokes.Add(stroke);
                }
                Assert.Equal(word.Length, strokes.Count);
                if (trailing is { } key)
                    strokes.Add(new KeyStroke(key));
                var weight = weighted ? frequency : 1;
                total += weight;
                var verdict = detector.Decide(strokes, current);
                var switched = verdict == Verdict.Switch;
                if (switched)
                    switches += weight;
                if (switched != detection && misses.Count < 50)
                    misses.Add($"`{KeyMap.Render(strokes, source)}` → `{KeyMap.Render(strokes, KeyMap.Other(current))}`; `{verdict}`");
            }
            return new QualityMeasure(name, switches, total, detection, target, misses);
        }
    }

    private static IEnumerable<(string Word, double Frequency)> TestWords(string name, Layout layout) =>
        File.ReadLines(Path.Combine(AppContext.BaseDirectory, "data", "test", name))
            .Select(word => (word, LangData.Load(layout).Frequency(word)));

    private sealed record QualityMeasure(string Name, double Switches, double Total, bool Detection,
        double Target, List<string> Misses)
    {
        public double Ratio => Total == 0 ? 0 : Switches / Total;
        public bool Passed => Total > 0 && (Detection ? Ratio >= Target : Ratio <= Target);
    }
}
