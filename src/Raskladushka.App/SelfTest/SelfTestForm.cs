namespace Raskladushka.App.SelfTest;

public sealed class SelfTestForm : Form
{
    private readonly TextBox _normal = new() { Dock = DockStyle.Top };
    private readonly TextBox _password = new() { Dock = DockStyle.Top, UseSystemPasswordChar = true };
    private readonly Label _status = new()
    {
        Dock = DockStyle.Top, Height = 70,
        Text = "Самопроверка набора. Не вводите текст и не переключайте окна до завершения."
    };
    private TextBox _active;
    private bool _running = true;

    public SelfTestForm()
    {
        Icon = Shell.AppIcons.App;
        Text = "Раскладушка — самопроверка";
        ClientSize = new Size(560, 210);
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(_password);
        Controls.Add(new Label { Text = "Поле пароля", Dock = DockStyle.Top, Height = 25 });
        Controls.Add(_normal);
        Controls.Add(new Label { Text = "Обычное поле", Dock = DockStyle.Top, Height = 25 });
        Controls.Add(_status);
        _active = _normal;
    }

    internal void Prepare(bool password)
    {
        _active = password ? _password : _normal;
        _active.Clear();
        _active.Focus();
    }
    internal string ReadText() => _active.Text;
    internal nint InputHandle => _active.Handle;
    internal void Complete() { _running = false; Close(); }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_running && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            _status.Text = "Дождитесь завершения самопроверки; потеря фокуса будет записана в отчёт.";
        }
        base.OnFormClosing(e);
    }
}
