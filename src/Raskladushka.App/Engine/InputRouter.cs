using Raskladushka.Core.Typing;
using Raskladushka.App.Dictation;

namespace Raskladushka.App.Engine;

public enum VoiceKeyState { Ready, NotInstalled, Busy }

public enum ResetCause { Other, Mouse, Focus, Gate }

public sealed class InputRouter
{
    private readonly ActivityGate _gate;
    private readonly SelectionConverter _selection;
    private readonly SelectionHint _hint;
    private readonly Func<int> _fixHotkey;
    private TypingController? _typing;
    private int _reset;
    private readonly object _resetSync = new();
    private (ResetCause Cause, long At)? _lastExternalReset;
    private readonly Func<bool> _fixOnAltTap;
    private bool _altTapArmed;
    public event Action<GateReason?>? TapBlocked;

    public (ResetCause Cause, long AgeMs)? LastExternalReset
    {
        get
        {
            lock (_resetSync)
                return _lastExternalReset is { } reset ? (reset.Cause, Environment.TickCount64 - reset.At) : null;
        }
    }
    private bool _closed;
    private readonly object _dictationGate = new();
    private readonly DictationWorker _dictation;
    private readonly Func<int> _dictationHotkey;
    private int _heldHotkey;
    private readonly Func<VoiceKeyState> _voiceState;
    private long? _missingPress;
    public event Action? VoiceMissing;

    public InputRouter(ActivityGate gate, SelectionConverter selection, Func<int> fixHotkey,
        DictationWorker dictation, Func<int> dictationHotkey, Func<bool> fixOnAltTap, SelectionHint hint,
        Func<VoiceKeyState> voiceState)
    {
        _voiceState = voiceState;
        _hint = hint;
        _fixOnAltTap = fixOnAltTap;
        _gate = gate;
        _selection = selection;
        _fixHotkey = fixHotkey;
        _dictation = dictation;
        _dictationHotkey = dictationHotkey;
    }
    public void SetController(TypingController typing) => Volatile.Write(ref _typing, typing);
    public void ResetExternal() => ResetExternal(ResetCause.Other);
    public void OnMouse(MouseInput m) => _hint.OnMouse(m);
    public void OnMouseButtonDown() => ResetExternal(ResetCause.Mouse);
    public void OnFocusChanged() => ResetExternal(ResetCause.Focus);
    public void OnGateChanged() => ResetExternal(ResetCause.Gate);

    private void ResetExternal(ResetCause cause)
    {
        lock (_resetSync)
        {
            _lastExternalReset = (cause, Environment.TickCount64);
            Interlocked.Exchange(ref _reset, 1);
        }
        lock (_dictationGate)
            if (_heldHotkey != 0 && !_gate.DictationAllowed) _ = _dictation.CancelAsync();
    }

    public bool OnKey(KeyEvent e, Modifiers mods)
    {
        var selectionLikely = _hint.Likely;
        _hint.OnKey(e, mods);
        var tap = false;
        if (!_fixOnAltTap()) _altTapArmed = false;
        else if (e.Vk is 0x12 or 0xA4 or 0xA5)
        {
            if (e.IsDown)
                _altTapArmed = !_altTapArmed && !e.Repeat && !mods.Shift && !mods.Ctrl && !mods.Win;
            else
            {
                tap = _altTapArmed;
                _altTapArmed = false;
            }
        }
        else if (e.IsDown) _altTapArmed = false;
        bool dictationKey;
        lock (_dictationGate)
        {
            dictationKey = e.Vk == (_heldHotkey != 0 ? _heldHotkey : _dictationHotkey());
            var repeat = dictationKey && e.IsDown && _heldHotkey != 0;
            if (!repeat && _heldHotkey != 0 && !_gate.DictationAllowed) _ = _dictation.CancelAsync();
            if (dictationKey)
            {
                if (e.IsDown)
                {
                    if (!repeat && !e.Repeat)
                    {
                        _heldHotkey = e.Vk;
                        _missingPress = null;
                        if (_gate.DictationAllowed)
                        {
                            var state = _voiceState();
                            if (state == VoiceKeyState.Ready) _ = _dictation.HotkeyDownAsync();
                            else if (state == VoiceKeyState.NotInstalled) _missingPress = Environment.TickCount64;
                        }
                    }
                }
                else
                {
                    _heldHotkey = 0;
                    _ = _dictation.HotkeyUpAsync();
                    var started = _missingPress;
                    _missingPress = null;
                    if (started is { } at && Environment.TickCount64 - at >= 400 &&
                        _voiceState() == VoiceKeyState.NotInstalled && _gate.DictationAllowed)
                        VoiceMissing?.Invoke();
                }
            }
            else if (_heldHotkey != 0)
            {
                _missingPress = null;
                if (e.IsDown) _ = _dictation.CancelAsync();
            }
        }
        var typing = Volatile.Read(ref _typing);
        if (typing is null)
        {
            if (tap) TapBlocked?.Invoke(null);
            return false;
        }
        var reset = Interlocked.Exchange(ref _reset, 0) != 0;
        var reason = _gate.Reason;
        var closed = reason != GateReason.None;
        if (tap && closed) TapBlocked?.Invoke(reason);
        if (reset || (closed && !_closed)) typing.ResetExternal();
        _closed = closed;
        if (!dictationKey && !closed && e.IsDown && e.Vk == _fixHotkey() && mods.Shift && !mods.Ctrl && !mods.Alt && !mods.Win)
        {
            typing.ResetExternal();
            _hint.Clear();
            _ = _selection.ConvertSelectionAsync(SelectionTrigger.Hotkey);
            return true;
        }
        var convertSelection = false;
        var consumed = !closed && typing.OnKey(e, mods, selectionLikely, out convertSelection);
        if (convertSelection)
        {
            _hint.Clear();
            _ = _selection.ConvertSelectionAsync(SelectionTrigger.AltTap);
        }
        return !dictationKey && consumed;
    }
}
