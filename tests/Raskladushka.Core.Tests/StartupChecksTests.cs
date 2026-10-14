using Raskladushka.Core.Shell;

namespace Raskladushka.Core.Tests;

public sealed class StartupChecksTests
{
    [Theory]
    [InlineData(new[] { 0x0419, 0x0409 }, MissingLayouts.None)]
    [InlineData(new[] { 0x0409 }, MissingLayouts.Russian)]
    [InlineData(new[] { 0x0419, 0x0422 }, MissingLayouts.English)]
    [InlineData(new int[] { }, MissingLayouts.Russian | MissingLayouts.English)]
    [InlineData(new[] { 0x0419, 0x0809 }, MissingLayouts.English)]
    public void ChecksExactLanguageIds(int[] ids, MissingLayouts expected) =>
        Assert.Equal(expected, LayoutCheck.Missing(ids));

    [Theory]
    [InlineData(MissingLayouts.None, "")]
    [InlineData(MissingLayouts.Russian, "ru")]
    [InlineData(MissingLayouts.English, "en")]
    [InlineData(MissingLayouts.Russian | MissingLayouts.English, "ru,en")]
    public void FormatsMissingLayouts(MissingLayouts missing, string expected) =>
        Assert.Equal(expected, LayoutCheck.LogValue(missing));

    [Theory]
    [InlineData(new[] { "Punto", "explorer" }, new[] { "Punto Switcher" })]
    [InlineData(new[] { "ps64ldr", "KeyRay" }, new[] { "Punto Switcher", "KeyRay" })]
    [InlineData(new[] { "punto", "ps64ldr" }, new[] { "Punto Switcher" })]
    [InlineData(new[] { "notepad" }, new string[] { })]
    [InlineData(new[] { "KeyRay64", "keyrayservice" }, new[] { "KeyRay" })]
    [InlineData(new[] { "KeyRay", "Punto" }, new[] { "Punto Switcher", "KeyRay" })]
    [InlineData(new[] { "" }, new string[] { })]
    public void FindsConflictsInStableOrder(string[] names, string[] expected) =>
        Assert.Equal(expected, ConflictingApps.Find(names));

    [Theory]
    [InlineData(false, MissingLayouts.None, "Раскладушка работает. Alt — исправить слово или выделенный текст. Правый Ctrl — голос (сначала его нужно скачать).")]
    [InlineData(true, MissingLayouts.None, "Раскладушка работает. Alt — исправить слово или выделенный текст. Правый Ctrl — голос.")]
    [InlineData(false, MissingLayouts.Russian, "Раскладушка работает. Alt — исправить слово или выделенный текст. Правый Ctrl — голос (сначала его нужно скачать).\nВ Windows нет русской раскладки — без неё автосмена не работает. Добавьте её: Параметры → Время и язык → Язык.")]
    [InlineData(true, MissingLayouts.English, "Раскладушка работает. Alt — исправить слово или выделенный текст. Правый Ctrl — голос.\nВ Windows нет английской раскладки (США) — без неё автосмена не работает. Добавьте её: Параметры → Время и язык → Язык.")]
    [InlineData(false, MissingLayouts.Russian | MissingLayouts.English, "Раскладушка работает. Alt — исправить слово или выделенный текст. Правый Ctrl — голос (сначала его нужно скачать).\nВ Windows нет русской и английской (США) раскладок — без них автосмена не работает. Добавьте их: Параметры → Время и язык → Язык.")]
    public void UsesExactFirstRunText(bool voiceInstalled, MissingLayouts missing, string expected) =>
        Assert.Equal(expected, StartupHints.FirstRun(voiceInstalled, missing));

    [Theory]
    [InlineData(false, MissingLayouts.None)]
    [InlineData(true, MissingLayouts.None)]
    [InlineData(false, MissingLayouts.Russian)]
    [InlineData(true, MissingLayouts.Russian)]
    [InlineData(false, MissingLayouts.English)]
    [InlineData(true, MissingLayouts.English)]
    [InlineData(false, MissingLayouts.Russian | MissingLayouts.English)]
    [InlineData(true, MissingLayouts.Russian | MissingLayouts.English)]
    public void FirstRunFitsBalloon(bool voiceInstalled, MissingLayouts missing) =>
        Assert.InRange(StartupHints.FirstRun(voiceInstalled, missing).Length, 1, StartupHints.MaxLength);

    [Theory]
    [InlineData(new[] { "KeyRay" }, "Запущен KeyRay. Две программы смены раскладки мешают друг другу — закройте одну из них.")]
    [InlineData(new[] { "Punto Switcher", "KeyRay" }, "Запущены Punto Switcher и KeyRay. Программы смены раскладки мешают друг другу — оставьте только одну.")]
    public void UsesExactConflictText(string[] apps, string expected) =>
        Assert.Equal(expected, StartupHints.Conflict(apps));
}
