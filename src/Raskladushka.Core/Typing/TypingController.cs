using Raskladushka.Core.Keys;

namespace Raskladushka.Core.Typing;

public sealed class TypingController
{
    private readonly object _sync = new();
    private readonly WordBuffer _buffer = new();
    private readonly LayoutDetector _detector;
    private readonly IExceptionSink _exceptions;
    private readonly IKeyOutput _output;
    private readonly ILayoutApi _layouts;
    private readonly Func<TypingOptions> _options;
    private readonly TimeProvider _time;
    private bool _altTapArmed;
    private TapDisarm _tapDisarm;
    private BufferReset _lastReset;
    private long _lastResetAt;
    private WordEnd? _undo;
    private Layout _undoLayout;
    private DecisionReason _undoReason;
    private Layout? _pendingLayout;
    private long _requestedAt;

    public TypingController(LayoutDetector detector, IExceptionSink exceptions, IKeyOutput output,
                            ILayoutApi layouts, Func<TypingOptions> options, TimeProvider time)
    {
        _detector = detector;
        _exceptions = exceptions;
        _output = output;
        _layouts = layouts;
        _options = options;
        _time = time;
    }

    public event Action<TypingStat>? Stat;
    public event Action<TypingEvent>? Event;
    public event Action<TapDiagnostic>? TapSkipped;

    public bool OnKey(KeyEvent e, Modifiers mods) => OnKey(e, mods, false, out _);

    public bool OnKey(KeyEvent e, Modifiers mods, bool selectionLikely, out bool convertSelection)
    {
        TypingEvent? stat;
        TapDiagnostic? diagnostic;
        bool swallow;
        lock (_sync)
            swallow = HandleKey(e, mods, _options(), selectionLikely, out convertSelection, out stat, out diagnostic);
        if (diagnostic is { } skipped) TapSkipped?.Invoke(skipped);
        if (stat is { } value)
        {
            Stat?.Invoke(value.Kind);
            Event?.Invoke(value);
        }
        return swallow;
    }

    public void ResetExternal()
    {
        lock (_sync)
            Reset(BufferReset.External);
    }

    private bool HandleKey(KeyEvent e, Modifiers mods, TypingOptions options, bool selectionLikely,
        out bool convertSelection, out TypingEvent? stat, out TapDiagnostic? diagnostic)
    {
        convertSelection = false;
        stat = null;
        diagnostic = null;
        if (options.FixOnAltTap)
        {
            if (e.Vk is 0x12 or 0xA4 or 0xA5)
            {
                if (!e.IsDown)
                {
                    var armed = _altTapArmed;
                    _altTapArmed = false;
                    var reason = TapSkipReason.NotArmed;
                    var applied = armed && ApplyFix(e.Vk, out stat, out reason);
                    if (armed && !applied && selectionLikely && reason is TapSkipReason.NoTarget or TapSkipReason.LayoutUnknown)
                    {
                        try
                        {
                            _output.ReleaseModifierWithCover(e.Vk);
                            convertSelection = true;
                            _tapDisarm = TapDisarm.None;
                            return true;
                        }
                        catch (Exception) { reason = TapSkipReason.ReleaseFailed; }
                    }
                    if (!applied && (reason != TapSkipReason.NotArmed ||
                        _tapDisarm is not (TapDisarm.OtherKey or TapDisarm.Modifiers or TapDisarm.SecondAlt)))
                        diagnostic = new(reason, _tapDisarm, _lastReset,
                            _lastReset == BufferReset.None ? null : (long)_time.GetElapsedTime(_lastResetAt).TotalMilliseconds,
                            selectionLikely);
                    _tapDisarm = TapDisarm.None;
                    return applied;
                }
                if (!_altTapArmed && !e.Repeat && !mods.Shift && !mods.Ctrl && !mods.Win)
                {
                    _altTapArmed = true;
                    _tapDisarm = TapDisarm.None;
                    return false;
                }
                if (e.Repeat)
                {
                    if (_altTapArmed) _tapDisarm = TapDisarm.Repeat;
                }
                else _tapDisarm = _altTapArmed ? TapDisarm.SecondAlt : TapDisarm.Modifiers;
                _altTapArmed = false;
                Reset(BufferReset.AltPress);
                return false;
            }
            if (e.IsDown && _altTapArmed)
            {
                _tapDisarm = TapDisarm.OtherKey;
                _altTapArmed = false;
            }
        }
        else _altTapArmed = false;
        if (!e.IsDown)
            return false;
        if (mods.Ctrl || mods.Alt || mods.Win)
        {
            Reset(BufferReset.Chord);
            return false;
        }
        if (!options.FixOnAltTap && e.Vk == options.FixHotkeyVk)
            return !mods.Shift && ApplyFix(null, out stat, out _);

        var key = KeyClassifier.Classify(e.Vk, e.Scan, e.Extended);
        switch (key.Kind)
        {
            case KeyKind.Reset:
                Reset(BufferReset.Key);
                return false;
            case KeyKind.Backspace:
                _undo = null;
                _buffer.OnBackspace();
                return false;
            case KeyKind.Char:
                _undo = null;
                var end = _buffer.OnChar(new KeyStroke(key.Key, mods.Shift, mods.Caps));
                var current = EffectiveLayout();
                if (end is null || !options.AutoSwitch || current is null)
                    return false;
                var decision = _detector.Explain(end.Word, current.Value);
                if (decision.Verdict != Verdict.Switch) return false;
                var other = KeyMap.Other(current.Value);
                _output.Replace(end.Word.Count, KeyMap.Render([.. end.Word, end.Terminator], other));
                RequestLayout(other);
                _undo = end;
                _undoLayout = current.Value;
                _undoReason = decision.Reason;
                stat = Describe(TypingStat.AutoSwitched, end.Word, current.Value, decision.Reason);
                return true;
            default:
                return false;
        }
    }

    private bool ApplyFix(int? releaseModifier, out TypingEvent? stat, out TapSkipReason reason)
    {
        stat = null;
        reason = TapSkipReason.LayoutUnknown;
        var current = EffectiveLayout();
        if (current is null)
            return false;
        reason = TapSkipReason.NoTarget;
        if (_undo is null && _buffer.PeekFixTarget() is null)
            return false;
        if (releaseModifier is { } vk)
        {
            try { _output.ReleaseModifierWithCover(vk); }
            catch (Exception) { reason = TapSkipReason.ReleaseFailed; return false; }
        }
        if (_undo is { } undo)
        {
            _output.Replace(undo.Word.Count + 1,
                KeyMap.Render([.. undo.Word, undo.Terminator], _undoLayout));
            RequestLayout(_undoLayout);
            _exceptions.Add(LayoutDetector.TrimToLetters(KeyMap.Render(undo.Word, _undoLayout), _undoLayout));
            _undo = null;
            stat = Describe(TypingStat.UndoApplied, undo.Word, KeyMap.Other(_undoLayout), _undoReason);
            return true;
        }
        var target = _buffer.PeekFixTarget();
        if (target is null)
            return false;
        var other = KeyMap.Other(current.Value);
        _output.Replace(target.Word.Count + target.Trailing.Count,
            KeyMap.Render([.. target.Word, .. target.Trailing], other));
        RequestLayout(other);
        stat = Describe(TypingStat.FixApplied, target.Word, current.Value,
            _detector.Explain(target.Word, current.Value).Reason);
        return true;
    }

    private static TypingEvent Describe(TypingStat kind, IReadOnlyList<KeyStroke> word, Layout from, DecisionReason reason) =>
        new(kind, from, KeyMap.Other(from), word.Count, reason,
            KeyMap.Render(word, from), KeyMap.Render(word, KeyMap.Other(from)));

    private Layout? EffectiveLayout()
    {
        var current = _layouts.GetCurrent();
        if (current is null)
            return null;
        if (_pendingLayout is { } pending)
        {
            // Window layout changes are asynchronous; subsequent keys must use the requested layout.
            if (current != pending && _time.GetElapsedTime(_requestedAt) < TimeSpan.FromMilliseconds(500))
                return pending;
            _pendingLayout = null;
        }
        return current;
    }

    private void RequestLayout(Layout target)
    {
        _layouts.Request(target);
        _pendingLayout = target;
        _requestedAt = _time.GetTimestamp();
    }

    private void Reset(BufferReset cause)
    {
        _lastReset = cause;
        _lastResetAt = _time.GetTimestamp();
        if (_altTapArmed) _tapDisarm = TapDisarm.Reset;
        _altTapArmed = false;
        _buffer.Reset();
        _undo = null;
        _pendingLayout = null;
    }
}
