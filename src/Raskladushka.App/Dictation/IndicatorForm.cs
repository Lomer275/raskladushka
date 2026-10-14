using Raskladushka.App.Native;
using Raskladushka.Core.Dictation;

namespace Raskladushka.App.Dictation;

public sealed class IndicatorForm : Form
{
    private readonly Label _label;
    public IndicatorForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(240, 40);
        BackColor = Color.FromArgb(32, 32, 32);
        Opacity = 0.9;
        _label = new Label
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.White, Font = new Font("Segoe UI", 11)
        };
        Controls.Add(_label);
        _ = Handle;
    }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x08000000 | 0x00000020 | 0x00000080;
            return parameters;
        }
    }
    public void SetState(IndicatorState state)
    {
        if (state == IndicatorState.Hidden) { Hide(); return; }
        _label.Text = state switch
        {
            IndicatorState.Recording => "● Запись",
            IndicatorState.LoadingModel => "Загружаю модель…",
            _ => "Распознаю…"
        };
        if (Visible) return;
        var area = Screen.FromHandle(NativeMethods.GetForegroundWindow()).WorkingArea;
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Bottom - Height - 60);
        Show();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) _label.Font.Dispose();
        base.Dispose(disposing);
    }
}
