using System.Diagnostics;
using System.Globalization;
using Raskladushka.Core.Config;
using Raskladushka.Core.Dictation;
using Raskladushka.Core.Voice;

namespace Raskladushka.App.Shell;

public sealed class VoiceForm : Form
{
    private readonly AppHost _host;
    private readonly Settings _settings;
    private readonly VoiceCatalog _catalog = VoiceCatalog.LoadEmbedded();
    private readonly TableLayoutPanel _layout = new()
    { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12) };
    private readonly TextBox _serverAddress = new() { Width = 600 };
    private readonly TextBox _serverKey = new() { Width = 600, UseSystemPasswordChar = true };
    private readonly Label _serverStatus = new() { AutoSize = true, MaximumSize = new Size(640, 0) };
    private readonly Button _checkServer = new() { AutoSize = true, Text = "Проверить" };
    private readonly Button _saveServer = new() { AutoSize = true, Text = "Сохранить" };
    private Task _serverWork = Task.CompletedTask;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private Task _work = Task.CompletedTask;
    private Task _suspend = Task.CompletedTask;
    private bool _closing;
    private bool _closed;
    private bool _allowClose;
    private VoiceVariant _selected;
    private string Dir => Path.GetFullPath(_settings.Whisper.Dir);
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    private const string Failed = "Голос не прошёл проверку, подробности в журнале.";
    private const string Ready = "Готово. Закройте окно, зажмите правый Ctrl и говорите.";

    public VoiceForm(AppHost host)
    {
        _host = host;
        _settings = host.Settings;
        Icon = AppIcons.App;
        Text = "Раскладушка — голосовой ввод";
        AutoScaleMode = AutoScaleMode.Font;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        StartPosition = FormStartPosition.CenterScreen;
        var sections = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 1 };
        var server = new GroupBox { Text = "Сервер распознавания", AutoSize = true, Dock = DockStyle.Top };
        var fields = new TableLayoutPanel
        { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12, 20, 12, 12) };
        fields.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(640, 0),
            Text = "Свой сервер whisper.cpp. Пока адрес пустой, голос распознаётся на этом компьютере." });
        fields.Controls.Add(new Label { AutoSize = true, Text = "Адрес" });
        fields.Controls.Add(_serverAddress);
        fields.Controls.Add(new Label { AutoSize = true, Text = "Ключ" });
        fields.Controls.Add(_serverKey);
        var buttons = new FlowLayoutPanel { AutoSize = true };
        buttons.Controls.Add(_checkServer);
        buttons.Controls.Add(_saveServer);
        fields.Controls.Add(buttons);
        fields.Controls.Add(_serverStatus);
        server.Controls.Add(fields);
        sections.Controls.Add(server);
        sections.Controls.Add(_layout);
        Controls.Add(sections);
        _serverAddress.Text = _settings.VoiceServer.Url;
        _serverKey.Text = _host.SecretProtector.Unprotect(_settings.VoiceServer.KeyProtected) ?? "";
        _checkServer.Click += (_, _) => BeginServerCheck();
        _saveServer.Click += (_, _) => SaveServer();
    }

    private VoiceServerSettings? ServerFields()
    {
        var address = _serverAddress.Text.Trim();
        if (address.Length > 0 && !VoiceServerAddress.TryParse(address, out _))
        {
            _serverStatus.Text = "Адрес должен начинаться с http:// или https://.";
            return null;
        }
        return new VoiceServerSettings
        {
            Url = address,
            KeyProtected = _serverKey.Text.Length == 0 ? "" : _host.SecretProtector.Protect(_serverKey.Text)
        };
    }

    private void SaveServer()
    {
        try
        {
            var settings = ServerFields();
            if (settings is null) return;
            _host.SaveVoiceServer(settings);
            _host.RefreshVoiceInstalled();
            if (settings.Url.Length > 0 && !_host.VoiceInstalled)
            {
                _serverStatus.Text = "Сохранено. Скачиваю голос для этого компьютера — он нужен, когда сервер недоступен.";
                BeginInstall(_selected, false);
            }
            else _serverStatus.Text = "Сохранено.";
        }
        catch (Exception) { _serverStatus.Text = "Не удалось сохранить настройки сервера."; }
    }

    private void BeginServerCheck()
    {
        try
        {
            var settings = ServerFields();
            if (settings is null) return;
            if (settings.Url.Length == 0)
            {
                _serverStatus.Text = "Адрес должен начинаться с http:// или https://.";
                return;
            }
            _checkServer.Enabled = false;
            _serverStatus.Text = "";
            _serverWork = Task.Run(async () =>
            {
                try
                {
                    var clip = VoiceVerifier.LoadSelftestClip();
                    var result = await _host.CheckVoiceServerAsync(settings, clip, _lifetime.Token).ConfigureAwait(false);
                    var text = result.Outcome switch
                    {
                        RemoteOutcome.Ok => string.Format(Russian, "Сервер ответил за {0:0.0} с: «{1}»", result.Elapsed.TotalSeconds, result.Text),
                        RemoteOutcome.KeyRejected => "Ключ не подходит.",
                        RemoteOutcome.Unreachable or RemoteOutcome.Timeout => "Сервер недоступен.",
                        RemoteOutcome.Busy => "Сервер занят, попробуйте позже.",
                        _ => result.Status is int code ? $"Сервер ответил ошибкой {code}." : "Сервер ответил непонятно."
                    };
                    Post(() => _serverStatus.Text = text);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
                catch (Exception) { Post(() => _serverStatus.Text = "Сервер ответил непонятно."); }
                finally { Post(() => _checkServer.Enabled = true); }
            });
        }
        catch (Exception) { _serverStatus.Text = "Не удалось подготовить ключ сервера."; }
    }

    protected override void OnShown(EventArgs e)
    {
        _host.VoiceSetupOpen = true;
        _saveServer.Enabled = false;
        _suspend = _host.Dictation.SuspendAsync();
        _work = Task.Run(async () =>
        {
            try
            {
                await _suspend.ConfigureAwait(false);
                _lifetime.Token.ThrowIfCancellationRequested();
                var manual = IsManual();
                var installed = WhisperLayout.IsInstalled(_settings.Whisper, File.Exists);
                var mode = InstalledMode();
                var offerModel = !manual && installed &&
                    WhisperLayout.HasBuild(_settings.Whisper, WhisperMode.Gpu, File.Exists) &&
                    _settings.Whisper.CpuModel.Length > 0 && !WhisperLayout.HasCpuModel(_settings.Whisper, File.Exists);
                var free = !manual && !installed ? FreeBytes(Dir) : 0;
                _selected = VoiceVariantChooser.Choose(GpuProbe.AdapterProviders(), GpuProbe.NvcudaPresent());
                Post(() => ShowInitial(manual, installed, mode, offerModel, free));
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { Unexpected(error); }
        });
        base.OnShown(e);
    }

    private bool IsManual()
    {
        string Target(string id) => _catalog.Packages(VoiceVariant.Gpu).Concat(_catalog.Packages(VoiceVariant.Cpu))
            .First(p => p.Id == id).Target;
        var settings = _settings.Whisper;
        return settings.GpuBuild != Target("build-cuda") || settings.CpuBuild != Target("build-cpu") ||
            settings.Model != Target("model-gpu") || settings.CpuModel != Target("model-cpu");
    }

    private WhisperMode InstalledMode() =>
        WhisperLayout.HasBuild(_settings.Whisper, WhisperMode.Gpu, File.Exists) &&
        WhisperLayout.ModelFor(_settings.Whisper, WhisperMode.Gpu, File.Exists) is not null ? WhisperMode.Gpu : WhisperMode.Cpu;

    internal static long FreeBytes(string dir) => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!).AvailableFreeSpace;
    private static string Gb(long bytes) => (bytes / (double)(1L << 30)).ToString("F1", Russian);
    private static string Mb(long bytes) => (bytes / (double)(1L << 20)).ToString("F0", Russian);

    private void ShowInitial(bool manual, bool installed, WhisperMode mode, bool offerModel, long free)
    {
        Clear();
        _saveServer.Enabled = true;
        if (manual) Label("Голос настроен вручную (раздел whisper в настройках), скачивание отключено.");
        else if (installed) Label(mode == WhisperMode.Gpu ? "Установлен: для видеокарты" : "Установлен: для процессора");
        if (installed)
        {
            Button("Проверить голос", () => BeginCheck(mode));
            if (offerModel) Button($"Скачать модель для процессора ({Mb(_catalog.DownloadBytes(VoiceVariant.CpuModel))} МБ)",
                () => BeginInstall(VoiceVariant.CpuModel, false));
        }
        else if (!manual)
        {
            foreach (var variant in new[] { VoiceVariant.Gpu, VoiceVariant.Cpu })
            {
                var radio = new RadioButton
                {
                    AutoSize = true, Checked = _selected == variant,
                    Text = $"{(variant == VoiceVariant.Gpu ? "Для видеокарты NVIDIA" : "Для процессора")} — скачать {Gb(_catalog.DownloadBytes(variant))} ГБ, на диске {Gb(_catalog.DiskBytes(variant))} ГБ"
                };
                radio.CheckedChanged += (_, _) => { if (radio.Checked) _selected = variant; };
                _layout.Controls.Add(radio);
            }
            Label($"Свободно на диске: {Gb(free)} ГБ");
            Button("Скачать", () => BeginInstall(_selected, false));
        }
    }

    private void BeginInstall(VoiceVariant variant, bool removeGpu)
    {
        Clear();
        var bar = new ProgressBar { Dock = DockStyle.Top, Maximum = 1000 };
        _layout.Controls.Add(bar);
        var status = Label($"Скачано 0 из {Mb(_catalog.DownloadBytes(variant))} МБ");
        StartOperation(async ct =>
        {
            var done = new Dictionary<string, long>();
            var progress = new ProgressSink(p =>
            {
                done[p.PackageId] = p.Done;
                var total = done.Values.Sum();
                Post(() =>
                {
                    if (bar.IsDisposed) return;
                    bar.Value = (int)Math.Clamp(total * 1000d / _catalog.DownloadBytes(variant), 0, 1000);
                    status.Text = $"Скачано {Mb(total)} из {Mb(_catalog.DownloadBytes(variant))} МБ";
                });
            });
            var result = "error";
            try
            {
                var installer = new VoiceInstaller(_catalog, new VoiceDownloader(VoiceHttp.Client, TimeProvider.System), FreeBytes);
                await installer.InstallAsync(variant, Dir, Path.Combine(AppContext.BaseDirectory, "vcrt"), progress, ct).ConfigureAwait(false);
                result = "ok";
            }
            catch (OperationCanceledException) { result = "cancelled"; throw; }
            catch (VoiceInstallException error)
            {
                result = error.Failure.ToString();
                Post(() => InstallError(error, variant, removeGpu));
                return;
            }
            finally { _host.Log.Info($"Voice install: variant={variant}; result={result}."); }
            await CheckAsync(variant == VoiceVariant.Gpu ? WhisperMode.Gpu : WhisperMode.Cpu,
                variant.ToString(), variant, removeGpu, ct).ConfigureAwait(false);
        }, () => BeginInstall(variant, removeGpu),
            "Скачивание остановлено. Скачанное сохранится до следующего раза.", "Скачать");
    }

    private void BeginCheck(WhisperMode mode)
    {
        BeginVerification(mode, "installed", null, false);
    }

    private void BeginVerification(WhisperMode mode, string label, VoiceVariant? variant, bool removeGpu)
    {
        Clear();
        StartOperation(ct => CheckAsync(mode, label, variant, removeGpu, ct),
            () => BeginVerification(mode, label, variant, removeGpu), "Проверка остановлена.", "Проверить ещё раз");
    }

    private async Task CheckAsync(WhisperMode mode, string label, VoiceVariant? variant, bool removeGpu, CancellationToken ct)
    {
        Post(() => { Clear(); Label("Проверяю голос…"); Button("Отмена", () => _operation?.Cancel()); });
        var result = await VoiceVerifier.VerifyAsync(_settings.Whisper, mode, _settings.Dictation.Language,
            _settings.Dictation.Prompt, 1, ct, label, _host.Log).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (result.Ok && removeGpu)
        {
            foreach (var package in _catalog.Packages(VoiceVariant.Gpu).Where(p => p.Id is "build-cuda" or "model-gpu"))
            {
                try
                {
                    var path = Path.Combine(Dir, package.Target);
                    if (package.Kind == PackageKind.Build) { if (Directory.Exists(path)) Directory.Delete(path, true); }
                    else File.Delete(path);
                }
                catch (Exception error) { _host.Log.Warn($"Voice GPU cleanup failed: {error.GetType().Name}."); }
            }
        }
        Post(() =>
        {
            Clear();
            if (result.Ok) Label(variant is null ? "Голос работает." : Ready);
            else if (variant == VoiceVariant.Gpu)
            {
                Label($"Видеокарта не подошла, подробности в журнале. Скачать вариант для процессора ({Gb(_catalog.DownloadBytes(VoiceVariant.CpuModel))} ГБ)? Файлы для видеокарты будут удалены.");
                Button("Скачать", () => BeginInstall(VoiceVariant.CpuModel, true));
            }
            else
            {
                Label(Failed);
                Button("Проверить ещё раз", () => BeginVerification(mode, label, variant, removeGpu));
            }
        });
    }

    private void InstallError(VoiceInstallException error, VoiceVariant variant, bool removeGpu)
    {
        Clear();
        Label(error.Message);
        if (error.Failure == VoiceFailure.Network)
        {
            Label("Можно скачать файлы вручную и положить в папку:");
            var files = new TextBox
            {
                ReadOnly = true, Multiline = true, Dock = DockStyle.Top, ScrollBars = ScrollBars.Both,
                WordWrap = false, Text = string.Join(Environment.NewLine, _catalog.Packages(variant).Select(p => $"{p.Url}  →  {p.File}"))
            };
            files.Height = files.Font.Height * (_catalog.Packages(variant).Count + 2);
            _layout.Controls.Add(files);
            Button("Открыть папку", () =>
            {
                try
                {
                    var path = Path.Combine(Dir, "downloads");
                    Directory.CreateDirectory(path);
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
                }
                catch (Exception ex) { Unexpected(ex); }
            });
        }
        Button(error.Failure == VoiceFailure.Network ? "Проверить ещё раз" : "Повторить", () => BeginInstall(variant, removeGpu));
    }

    private void StartOperation(Func<CancellationToken, Task> action, Action retry, string cancelledText, string retryText)
    {
        _saveServer.Enabled = false;
        _operation?.Dispose();
        _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var ct = _operation.Token;
        Button("Отмена", () => _operation?.Cancel());
        _work = Task.Run(async () =>
        {
            try { await action(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Post(() =>
                {
                    Clear();
                    Label(cancelledText);
                    Button(retryText, retry);
                });
            }
            catch (Exception error) { Unexpected(error); }
            finally { Post(() => _saveServer.Enabled = true); }
        });
    }

    private void Unexpected(Exception error)
    {
        _host.Log.Error("Voice setup failed", error);
        Post(() => { Clear(); Label("Не удалось поставить голос. Подробности в журнале."); });
    }

    private void Clear()
    {
        while (_layout.Controls.Count > 0) _layout.Controls[0].Dispose();
    }
    private Label Label(string text)
    {
        var label = new Label { AutoSize = true, Text = text, MaximumSize = new Size(640, 0), Margin = new Padding(3, 6, 3, 6) };
        _layout.Controls.Add(label);
        return label;
    }
    private void Button(string text, Action action)
    {
        var button = new Button { AutoSize = true, Text = text };
        button.Click += (_, _) => action();
        _layout.Controls.Add(button);
    }
    private void Post(Action action)
    {
        if (_closed || _closing) return;
        try { BeginInvoke(new Action(() => { if (!_closed && !_closing) action(); })); }
        catch (InvalidOperationException) { }
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        var immediate = e.CloseReason is CloseReason.ApplicationExitCall or CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing;
        if (!_allowClose && !immediate)
        {
            e.Cancel = true;
            base.OnFormClosing(e);
            if (_closing) return;
            _closing = true;
            _lifetime.Cancel();
            await Task.WhenAll(_work, _serverWork);
            _allowClose = true;
            Close();
            return;
        }
        _lifetime.Cancel();
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _closed = true;
        _host.VoiceSetupOpen = false;
        _ = RestoreAsync();
        base.OnFormClosed(e);
    }

    private async Task RestoreAsync()
    {
        try { await _suspend.ConfigureAwait(false); }
        catch (Exception error) { _host.Log.Error("Voice suspension failed", error); }
        try { await _host.Dictation.ResumeAsync().ConfigureAwait(false); }
        catch (Exception error) { _host.Log.Error("Voice resume failed", error); }
        try { await Task.Run(_host.RefreshVoiceInstalled).ConfigureAwait(false); }
        catch (Exception error) { _host.Log.Error("Voice installed refresh failed", error); }
        await Task.WhenAll(_work, _serverWork).ConfigureAwait(false);
        _operation?.Dispose();
        _lifetime.Dispose();
    }

    private sealed class ProgressSink(Action<VoiceProgress> report) : IProgress<VoiceProgress>
    {
        public void Report(VoiceProgress value) => report(value);
    }
}
