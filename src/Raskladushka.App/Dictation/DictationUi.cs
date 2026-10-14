using Raskladushka.Core.Dictation;

namespace Raskladushka.App.Dictation;

public sealed class DictationUi(IndicatorForm form, Action<string, NotifyAction> notify, Action idle) : IDictationUi
{
    private readonly IndicatorForm _form = form;
    private readonly Action<string, NotifyAction> _notify = notify;
    private readonly Action _idle = idle;
    private int _busy;
    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public void SetIndicator(IndicatorState state)
    {
        if (state == IndicatorState.Hidden)
        {
            _idle();
            Volatile.Write(ref _busy, 0);
        }
        else Volatile.Write(ref _busy, 1);
        Post(() => _form.SetState(state));
    }
    public void Notify(string message, NotifyAction action = NotifyAction.None) => Post(() => _notify(message, action));
    private void Post(Action action)
    {
        // The machine holds its lock here; synchronous Invoke can deadlock the UI thread.
        if (_form.IsDisposed) return;
        try { _form.BeginInvoke(new Action(() => { if (!_form.IsDisposed) action(); })); }
        catch (InvalidOperationException) { }
    }
}
