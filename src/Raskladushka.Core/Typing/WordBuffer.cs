using Raskladushka.Core.Keys;

namespace Raskladushka.Core.Typing;

public sealed record WordEnd(IReadOnlyList<KeyStroke> Word, KeyStroke Terminator);
public sealed record FixTarget(IReadOnlyList<KeyStroke> Word, IReadOnlyList<KeyStroke> Trailing);

public sealed class WordBuffer
{
    public const int MaxWordLength = 64;

    private readonly List<KeyStroke> _current = [];
    private readonly List<KeyStroke> _previous = [];
    private readonly List<KeyStroke> _trailing = [];
    private bool _ignoringLongWord;

    public WordEnd? OnChar(KeyStroke stroke)
    {
        var isWordKey = KeyMap.IsWordKey(stroke.Key);
        if (_ignoringLongWord)
        {
            if (!isWordKey)
                _ignoringLongWord = false;
            return null;
        }

        if (isWordKey)
        {
            if (_current.Count == 0)
            {
                _previous.Clear();
                _trailing.Clear();
            }

            if (_current.Count == MaxWordLength)
            {
                Reset();
                _ignoringLongWord = true;
                return null;
            }

            _current.Add(stroke);
            return null;
        }

        if (_current.Count > 0)
        {
            _previous.Clear();
            _previous.AddRange(_current);
            _current.Clear();
            _trailing.Clear();
            _trailing.Add(stroke);
            return new WordEnd(_previous.ToArray(), stroke);
        }

        if (_previous.Count > 0)
            _trailing.Add(stroke);
        return null;
    }

    public void OnBackspace()
    {
        if (_current.Count > 0)
        {
            _current.RemoveAt(_current.Count - 1);
            return;
        }

        if (_trailing.Count == 0)
            return;

        _trailing.RemoveAt(_trailing.Count - 1);
        if (_trailing.Count == 0)
        {
            _current.AddRange(_previous);
            _previous.Clear();
        }
    }

    public void Reset()
    {
        _current.Clear();
        _previous.Clear();
        _trailing.Clear();
        _ignoringLongWord = false;
    }

    public FixTarget? PeekFixTarget()
    {
        if (_current.Count > 0)
            return new FixTarget(_current.ToArray(), Array.Empty<KeyStroke>());
        if (_previous.Count > 0)
            return new FixTarget(_previous.ToArray(), _trailing.ToArray());
        return null;
    }
}
