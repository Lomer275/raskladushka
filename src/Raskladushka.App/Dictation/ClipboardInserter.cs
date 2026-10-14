using Raskladushka.App.Engine;
using Raskladushka.App.Input;
using Raskladushka.App.Shell;
using Raskladushka.Core.Dictation;

namespace Raskladushka.App.Dictation;

public sealed class ClipboardInserter(StaWorker worker, ClipboardHelper clipboard, InputInjector output) : ITextInserter
{
    private readonly StaWorker _worker = worker;
    private readonly ClipboardHelper _clipboard = clipboard;
    private readonly InputInjector _output = output;

    public Task InsertAsync(string text) => _worker.RunAsync(() =>
    {
        var saved = _clipboard.Save();
        try
        {
            _clipboard.SetText(text);
            _output.SendChord(0x11, 0x56);
            Thread.Sleep(ClipboardHelper.PasteHoldMs);
        }
        finally { _clipboard.Restore(saved); }
    });
}
