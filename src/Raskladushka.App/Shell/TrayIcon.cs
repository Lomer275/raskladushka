using System.Diagnostics;
using Raskladushka.App.SelfTest;
using Raskladushka.Core.Dictation;
using Raskladushka.Core.Updates;

namespace Raskladushka.App.Shell;

public sealed class TrayIcon : IDisposable
{
    private readonly AppHost _host;
    private readonly Control _dispatcher = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _pause;
    private readonly ToolStripMenuItem _autoSwitch;
    private readonly ToolStripMenuItem _voice;
    private VoiceForm? _voiceForm;
    private readonly ToolStripMenuItem _logWords;
    private readonly ToolStripMenuItem _selfTest;
    private readonly ToolStripMenuItem _autostart;
    private readonly ToolStripMenuItem _updateNotify;
    private readonly Icon _icon;
    private readonly Icon _pausedIcon;
    private AboutForm? _aboutForm;
    private bool? _iconPaused;
    private bool _disposed;
    private NotifyAction _notificationAction;

    public TrayIcon(AppHost host)
    {
        _host = host;
        _icon = AppIcons.Load("tray.ico", SystemInformation.SmallIconSize);
        _pausedIcon = AppIcons.Load("tray-paused.ico", SystemInformation.SmallIconSize);
        _dispatcher.CreateControl();
        _pause = Add("Пауза", () => _host.Gate.Paused = !_host.Gate.Paused);
        _autoSwitch = Add("Автосмена раскладки", _host.ToggleAutoSwitch);
        _voice = Add("Голосовой ввод: скачать…", OpenVoiceSetup);
        _logWords = Add("Записывать исправленные слова", _host.ToggleLogWords);
        Add("Открыть настройки", () =>
        {
            var start = new ProcessStartInfo("notepad.exe") { UseShellExecute = false };
            start.ArgumentList.Add(_host.SettingsPath);
            Process.Start(start)?.Dispose();
        });
        Add("Открыть журнал", () =>
        {
            Directory.CreateDirectory(_host.Log.DirectoryPath);
            Process.Start(new ProcessStartInfo(_host.Log.DirectoryPath) { UseShellExecute = true })?.Dispose();
        });
        _selfTest = Add("Самопроверка", () => _ = RunSelfTestAsync());
        _autostart = Add("Автозапуск", () =>
        {
            if (Autostart.IsEnabled) Autostart.Disable();
            else Autostart.Enable(Environment.ProcessPath ?? throw new InvalidOperationException());
        });
        _updateNotify = Add("Сообщать об обновлениях", _host.ToggleUpdateNotify);
        Add("Проверить обновления", () => _ = CheckUpdatesAsync());
        Add("О программе", OpenAbout);
        Add("Выход", Application.Exit);
        _tray = new NotifyIcon { Text = "Раскладушка", ContextMenuStrip = _menu };
        _menu.Opening += (_, _) => Refresh();
        _host.Notification += Notify;
        _tray.BalloonTipClicked += (_, _) => ExecuteNotificationAction();
        _host.Gate.Changed += PostRefresh;
        Refresh();
        _tray.Visible = true;
    }

    private ToolStripMenuItem Add(string text, Action action)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) =>
        {
            try { action(); Refresh(); }
            catch (Exception error)
            {
                _host.Log.Error("Tray action failed", error);
                Notify(text == "Автозапуск" ? "Не удалось изменить автозапуск." : "Не удалось выполнить действие.");
            }
        };
        _menu.Items.Add(item);
        return item;
    }

    private async Task CheckUpdatesAsync()
    {
        try { await _host.Updates.CheckNowAsync(); }
        catch (Exception error) { _host.Log.Error("Manual update check failed", error); }
    }

    private void OpenAbout()
    {
        if (_aboutForm is { IsDisposed: false })
        {
            if (_aboutForm.WindowState == FormWindowState.Minimized) _aboutForm.WindowState = FormWindowState.Normal;
            _aboutForm.BringToFront();
            _aboutForm.Activate();
            return;
        }
        _aboutForm = new AboutForm(_host);
        _aboutForm.Show();
    }

    private void OpenVoiceSetup()
    {
        if (_voiceForm is { IsDisposed: false })
        {
            if (_voiceForm.WindowState == FormWindowState.Minimized) _voiceForm.WindowState = FormWindowState.Normal;
            _voiceForm.BringToFront();
            _voiceForm.Activate();
            return;
        }
        _voiceForm = new VoiceForm(_host);
        _voiceForm.Show();
    }

    private void PostRefresh() => Post(Refresh);
    private void Refresh()
    {
        _voice.Text = _host.VoiceInstalled ? "Голосовой ввод…" : "Голосовой ввод: скачать…";
        _pause.Checked = _host.Gate.Paused;
        _autoSwitch.Checked = _host.Settings.Layout.AutoSwitch;
        _logWords.Checked = _host.Settings.Diagnostics.LogWords;
        _updateNotify.Checked = _host.Settings.Updates.Notify;
        try { _autostart.Checked = Autostart.IsEnabled; }
        catch (Exception error)
        {
            _host.Log.Error("Autostart read failed", error);
            Notify("Не удалось прочитать настройки автозапуска.");
        }
        if (_iconPaused == _pause.Checked) return;
        _tray.Icon = _pause.Checked ? _pausedIcon : _icon;
        _iconPaused = _pause.Checked;
    }

    public void Notify(string message, NotifyAction action = NotifyAction.None) => Post(() =>
    {
        _notificationAction = NotifyAction.None;
        _tray.ShowBalloonTip(5000, "Раскладушка", message, ToolTipIcon.Info);
        _notificationAction = action;
    });

    private void ExecuteNotificationAction()
    {
        var action = _notificationAction;
        _notificationAction = NotifyAction.None;
        try
        {
            if (action == NotifyAction.OpenMicrophoneSettings)
                Process.Start(new ProcessStartInfo("ms-settings:privacy-microphone") { UseShellExecute = true })?.Dispose();
            else if (action == NotifyAction.OpenVoiceSetup)
                OpenVoiceSetup();
            else if (action == NotifyAction.OpenReleasePage)
            {
                var uri = _host.Updates.LatestPage;
                if (uri is not null && uri.AbsoluteUri.StartsWith(UpdateCheck.PagePrefix, StringComparison.Ordinal))
                    Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
            }
        }
        catch (Exception error) { _host.Log.Error("Notification action failed", error); }
    }
    private void Post(Action action)
    {
        if (_disposed) return;
        try { _dispatcher.BeginInvoke(new Action(() => { if (!_disposed) action(); })); }
        catch (InvalidOperationException) { }
    }

    public async Task<bool> RunSelfTestAsync()
    {
        if (!_selfTest.Enabled) return false;
        _selfTest.Enabled = false;
        using var form = new SelfTestForm();
        try
        {
            form.Show();
            var runner = new SelfTestRunner(_host, form);
            var report = await Task.Run(() =>
            {
                var result = runner.Run();
                result.Save();
                return result;
            });
            Notify($"Самопроверка: {report.Passed} из {report.Total}");
            _host.Log.Info($"Selftest: {report.Passed}/{report.Total}.");
            return report.Success;
        }
        catch (Exception error)
        {
            _host.Log.Error("Selftest failed", error);
            Notify("Не удалось завершить самопроверку или записать отчёт.");
            return false;
        }
        finally
        {
            form.Complete();
            if (!_disposed) { _selfTest.Enabled = true; Refresh(); }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _host.Notification -= Notify;
        _host.Gate.Changed -= PostRefresh;
        _tray.Visible = false;
        _tray.Dispose();
        _icon.Dispose();
        _pausedIcon.Dispose();
        _aboutForm?.Dispose();
        _menu.Dispose();
        _dispatcher.Dispose();
    }
}
