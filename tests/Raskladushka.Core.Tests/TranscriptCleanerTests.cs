using Raskladushka.Core.Dictation;

namespace Raskladushka.Core.Tests;

public sealed class TranscriptCleanerTests
{
    [Theory]
    [InlineData(" Привет!\n Проверяю ввод\n Сегодня 8 октября\n", "Привет! Проверяю ввод. Сегодня 8 октября ")]
    [InlineData("привет\nмир", "привет мир ")]
    [InlineData("a  b", "a b ")]
    [InlineData("Это продолжение следует из текста", "Это продолжение следует из текста ")]
    [InlineData("123\r\nСегодня", "123. Сегодня ")]
    [InlineData("готово.\nДалее", "готово. Далее ")]
    [InlineData("один\n2", "один 2 ")]
    public void JoinsSegments(string raw, string expected) => Assert.Equal(expected, TranscriptCleaner.Clean(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\n \n")]
    [InlineData("Продолжение следует...")]
    [InlineData(" Субтитры сделал DimaTorzok\n")]
    [InlineData("Субтитры создавал DimaTorzok")]
    [InlineData("Субтитры делал DimaTorzok")]
    [InlineData("Редактор субтитров А.Семкин Корректор А.Егорова")]
    [InlineData("СПАСИБО за просмотр!")]
    [InlineData("Thanks for watching.")]
    [InlineData("Подписывайтесь на канал")]
    public void RejectsEmptyAndHallucinatedText(string? raw) => Assert.Null(TranscriptCleaner.Clean(raw));
}
