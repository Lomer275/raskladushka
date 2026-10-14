namespace Raskladushka.Core.Keys;

public static class KeyMap
{
    // Characters follow PhysKey order, including the trailing Space.
    private const string EnNormal = "`1234567890-=qwertyuiop[]\\asdfghjkl;'zxcvbnm,./ ";
    private const string EnShifted = "~!@#$%^&*()_+QWERTYUIOP{}|ASDFGHJKL:\"ZXCVBNM<>? ";
    private const string RuNormal = "ё1234567890-=йцукенгшщзхъ\\фывапролджэячсмитьбю. ";
    private const string RuShifted = "Ё!\"№;%:?*()_+ЙЦУКЕНГШЩЗХЪ/ФЫВАПРОЛДЖЭЯЧСМИТЬБЮ, ";

    public static Layout Other(Layout layout) => layout == Layout.En ? Layout.Ru : Layout.En;

    public static Layout GuessLayout(string text)
    {
        var en = 0;
        var ru = 0;
        foreach (var character in text)
        {
            if (character is >= 'a' and <= 'z' or >= 'A' and <= 'Z') en++;
            else if (character is >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ё' or 'Ё') ru++;
        }
        return ru > en ? Layout.Ru : Layout.En;
    }

    public static char ToChar(KeyStroke stroke, Layout layout)
    {
        var shifted = IsLetter(stroke.Key, layout) ? stroke.Shift ^ stroke.Caps : stroke.Shift;
        var table = layout == Layout.En
            ? (shifted ? EnShifted : EnNormal)
            : (shifted ? RuShifted : RuNormal);
        return table[(int)stroke.Key];
    }

    public static bool IsLetter(PhysKey key, Layout layout) =>
        char.IsLetter((layout == Layout.En ? EnNormal : RuNormal)[(int)key]);

    public static bool IsWordKey(PhysKey key) => IsLetter(key, Layout.En) || IsLetter(key, Layout.Ru);

    public static string Render(IReadOnlyList<KeyStroke> strokes, Layout layout)
    {
        var characters = new char[strokes.Count];
        for (var i = 0; i < strokes.Count; i++)
            characters[i] = ToChar(strokes[i], layout);
        return new string(characters);
    }

    public static bool TryFromChar(char c, Layout layout, out KeyStroke stroke)
    {
        var normal = layout == Layout.En ? EnNormal : RuNormal;
        var index = normal.IndexOf(c);
        if (index >= 0)
        {
            stroke = new KeyStroke((PhysKey)index);
            return true;
        }

        var shifted = layout == Layout.En ? EnShifted : RuShifted;
        index = shifted.IndexOf(c);
        if (index >= 0)
        {
            stroke = new KeyStroke((PhysKey)index, Shift: true);
            return true;
        }

        stroke = default;
        return false;
    }

    public static string Convert(string text, Layout from, Layout to)
    {
        var characters = new char[text.Length];
        for (var i = 0; i < text.Length; i++)
            characters[i] = TryFromChar(text[i], from, out var stroke)
                ? ToChar(stroke, to)
                : text[i];
        return new string(characters);
    }
}
