using Raskladushka.App.Input;
using Raskladushka.App.Shell;
using Raskladushka.Core.Keys;
using Raskladushka.Core.Typing;

namespace Raskladushka.App.Engine;

public enum SelectionTrigger { Hotkey, AltTap }

public sealed class SelectionConverter
{
    // VS Code webviews copy twice on Ctrl+Insert; the second copy can overwrite the conversion before paste.
    private static readonly CopyWaitOptions CopyOptions = new(FirstMs: 1000, QuietMs: 200, SettleCapMs: 2000, PollMs: 10);
    private readonly StaWorker _worker;
    private readonly ClipboardHelper _clipboard;
    private readonly InputInjector _output;
    private readonly ILayoutApi _layouts;
    private readonly Action<string> _log;
    private int _busy;

    public SelectionConverter(StaWorker worker, ClipboardHelper clipboard, InputInjector output, ILayoutApi layouts, Action<string> log)
    {
        _worker = worker;
        _clipboard = clipboard;
        _output = output;
        _layouts = layouts;
        _log = log;
    }

    public async Task ConvertSelectionAsync(SelectionTrigger trigger)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        try { await _worker.RunAsync(() => ConvertSelection(trigger)).ConfigureAwait(false); }
        catch (Exception error) { Log($"failed: {error.GetType().Name}; trigger={trigger}."); }
        finally { Volatile.Write(ref _busy, 0); }
    }

    private void Log(string message)
    {
        try { _log(message); } catch (Exception) { }
    }

    private void ConvertSelection(SelectionTrigger trigger)
    {
        object? snapshot = null;
        Layout? target = null;
        var length = 0;
        CopyWaitResult copy = default;
        var hold = 0;
        var overwritten = false;
        try
        {
            _output.ReleaseShift();
            snapshot = _clipboard.Save();
            var sequence = _clipboard.SequenceNumber;
            _output.SendChord(0x11, 0x2D);
            copy = CopyWait.Run(() => _clipboard.SequenceNumber, sequence, CopyOptions, TimeProvider.System, Thread.Sleep);
            if (copy.Status == CopyWaitStatus.NothingCopied)
            {
                Log($"nothing copied; trigger={trigger}; reason=timeout; waitedMs={copy.ElapsedMs}.");
                return;
            }
            if (copy.Status == CopyWaitStatus.Busy)
            {
                Log($"clipboard busy; trigger={trigger}; copyMs={copy.FirstMs}; lastCopyMs={copy.LastMs}; copies={copy.Changes}.");
                return;
            }
            var text = _clipboard.GetText();
            if (string.IsNullOrEmpty(text) || text.Length > 100_000)
            {
                var reason = string.IsNullOrEmpty(text) ? "empty" : "tooLong";
                Log($"nothing copied; trigger={trigger}; reason={reason}; copyMs={copy.FirstMs}; copies={copy.Changes}.");
                return;
            }
            length = text.Length;
            var from = KeyMap.GuessLayout(text);
            var to = KeyMap.Other(from);
            _clipboard.SetText(KeyMap.Convert(text, from, to));
            var pasteSequence = _clipboard.SequenceNumber;
            _output.SendChord(0x11, 0x56);
            hold = CopyWait.PasteHoldMs(copy.LastMs, ClipboardHelper.PasteHoldMs, 2000);
            Thread.Sleep(hold);
            overwritten = _clipboard.SequenceNumber != pasteSequence;
            target = to;
        }
        finally { _clipboard.Restore(snapshot); }
        if (target is { } layout)
        {
            _layouts.Request(layout);
            Log($"converted; {KeyMap.Other(layout).ToString().ToLowerInvariant()}->{layout.ToString().ToLowerInvariant()}; length={length}; trigger={trigger}; copyMs={copy.FirstMs}; lastCopyMs={copy.LastMs}; copies={copy.Changes}; holdMs={hold}; overwritten={overwritten}.");
        }
    }
}
