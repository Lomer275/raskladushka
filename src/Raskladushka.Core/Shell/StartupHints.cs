namespace Raskladushka.Core.Shell;

public static class StartupHints
{
    public const int MaxLength = 255;

    public static string FirstRun(bool voiceInstalled, MissingLayouts missing)
    {
        var text = voiceInstalled
            ? "Раскладушка работает. Alt — исправить слово или выделенный текст. Правый Ctrl — голос."
            : "Раскладушка работает. Alt — исправить слово или выделенный текст. Правый Ctrl — голос (сначала его нужно скачать).";
        var warning = missing switch
        {
            MissingLayouts.None => "",
            MissingLayouts.Russian => "В Windows нет русской раскладки — без неё автосмена не работает. Добавьте её: Параметры → Время и язык → Язык.",
            MissingLayouts.English => "В Windows нет английской раскладки (США) — без неё автосмена не работает. Добавьте её: Параметры → Время и язык → Язык.",
            _ => "В Windows нет русской и английской (США) раскладок — без них автосмена не работает. Добавьте их: Параметры → Время и язык → Язык."
        };
        return warning.Length == 0 ? text : text + "\n" + warning;
    }

    public static string Conflict(IReadOnlyList<string> apps) => apps.Count == 1
        ? $"Запущен {apps[0]}. Две программы смены раскладки мешают друг другу — закройте одну из них."
        : $"Запущены {apps[0]} и {apps[1]}. Программы смены раскладки мешают друг другу — оставьте только одну.";
}
