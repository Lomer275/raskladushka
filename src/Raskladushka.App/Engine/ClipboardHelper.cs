using System.Runtime.InteropServices;
using Raskladushka.App.Native;
using Raskladushka.Core.Shell;

namespace Raskladushka.App.Engine;

public sealed class ClipboardHelper
{
    // VS Code pastes into webviews via a round trip, so the clipboard must survive it.
    public const int PasteHoldMs = 500;

    public uint SequenceNumber => NativeMethods.GetClipboardSequenceNumber();

    public object? Save()
    {
        try
        {
            return Retry<object>(() =>
            {
                var source = Clipboard.GetDataObject();
                var snapshot = new DataObject();
                if (source is null) return snapshot;
                // Materialize every format before Ctrl+C invalidates the original clipboard object.
                foreach (var format in source.GetFormats(false))
                {
                    var data = source.GetData(format, false);
                    if (data is null) throw new InvalidOperationException("Формат буфера обмена недоступен.");
                    if (data is Image image) data = image.Clone();
                    else if (data is Stream stream)
                    {
                        var copy = new MemoryStream();
                        var position = stream.CanSeek ? stream.Position : 0;
                        if (stream.CanSeek) stream.Position = 0;
                        try { stream.CopyTo(copy); }
                        finally { if (stream.CanSeek) stream.Position = position; }
                        copy.Position = 0;
                        data = copy;
                    }
                    snapshot.SetData(format, false, data);
                }
                return snapshot;
            });
        }
        catch (Exception)
        {
            try { return GetText(); }
            catch (Exception) { return null; }
        }
    }

    public void Restore(object? snapshot)
    {
        if (snapshot is null) return;
        try
        {
            if (snapshot is string text) SetText(text);
            else Retry(() =>
            {
                if (snapshot is IDataObject data && data.GetFormats(false).Length == 0) Clipboard.Clear();
                else
                {
                    if (snapshot is IDataObject dataObject) AddPrivacyFormats(dataObject);
                    Clipboard.SetDataObject(snapshot, true, 0, 0);
                }
                return true;
            });
        }
        catch (Exception) { }
    }

    public string? GetText() => Retry(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);

    public void SetText(string text) => Retry(() =>
    {
        if (text.Length == 0) Clipboard.Clear();
        else
        {
            var data = new DataObject(DataFormats.UnicodeText, text);
            AddPrivacyFormats(data);
            Clipboard.SetDataObject(data, true, 0, 0);
        }
        return true;
    });

    private static void AddPrivacyFormats(IDataObject data)
    {
        foreach (var (format, bytes) in ClipboardPrivacy.Formats)
            data.SetData(format, false, new MemoryStream(bytes));
    }

    private static T Retry<T>(Func<T> action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return action(); }
            catch (ExternalException) when (attempt < 4) { Thread.Sleep(20); }
        }
    }
}
