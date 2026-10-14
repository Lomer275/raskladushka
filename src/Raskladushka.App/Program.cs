using System.Text.Json;
using Raskladushka.Core.Config;
using Raskladushka.Core.Dictation;
using Raskladushka.Core.Voice;
using Raskladushka.App.Shell;

namespace Raskladushka.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--exit", StringComparer.OrdinalIgnoreCase))
            return ExitSignal.RequestExit(TimeSpan.FromSeconds(10));
        if (args.Contains("--voice-check", StringComparer.OrdinalIgnoreCase))
            return VoiceCheckAsync(args).GetAwaiter().GetResult();
        ApplicationConfiguration.Initialize();
        using var mutex = new Mutex(true, ExitSignal.MutexName, out var first);
        var selfTest = args.Contains("--selftest", StringComparer.OrdinalIgnoreCase);
        if (!first)
        {
            if (selfTest) return 2;
            MessageBox.Show("Раскладушка уже запущена.", "Раскладушка");
            return 0;
        }
        using var log = new FileLog();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        ThreadExceptionEventHandler uiError = (_, e) => log.Error("UI exception", e.Exception);
        UnhandledExceptionEventHandler domainError = (_, e) =>
        {
            if (e.ExceptionObject is Exception error) log.Error("Unhandled exception", error);
            else log.Error("Unhandled non-Exception error.");
            log.Flush();
        };
        EventHandler<UnobservedTaskExceptionEventArgs> taskError = (_, e) =>
        {
            log.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
        Application.ThreadException += uiError;
        AppDomain.CurrentDomain.UnhandledException += domainError;
        TaskScheduler.UnobservedTaskException += taskError;
        log.Info($"Application started; version={Application.ProductVersion}.");
        try
        {
            using var host = new AppHost(log);
            using var tray = new TrayIcon(host);
            using var exitSignal = new ExitSignal(SynchronizationContext.Current!, () => { log.Info("Exit requested."); Application.Exit(); });
            host.Start();
            var exitCode = 0;
            if (selfTest)
            {
                exitCode = 1;
                EventHandler? run = null;
                run = async (_, _) =>
                {
                    Application.Idle -= run;
                    exitCode = await tray.RunSelfTestAsync() ? 0 : 1;
                    Application.Exit();
                };
                Application.Idle += run;
            }
            try { Application.Run(); }
            finally { host.Dispose(); }
            return exitCode;
        }
        catch (Exception error)
        {
            log.Error("Application failure", error);
            MessageBox.Show("Не удалось запустить или завершить Раскладушку. Подробности в журнале.", "Раскладушка");
            return 1;
        }
        finally
        {
            log.Info("Application stopped.");
            log.Flush();
            Application.ThreadException -= uiError;
            AppDomain.CurrentDomain.UnhandledException -= domainError;
            TaskScheduler.UnobservedTaskException -= taskError;
            mutex.ReleaseMutex();
        }
    }
    private static async Task<int> VoiceCheckAsync(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var flags = 0;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--voice-check", StringComparison.OrdinalIgnoreCase)) { flags++; continue; }
            var key = args[i];
            if (key is not ("--variant" or "--dir" or "--runtime" or "--out") ||
                ++i >= args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--") ||
                !options.TryAdd(key, args[i])) return 2;
        }
        if (flags != 1 || options.Count != 4 || options["--variant"] is not ("cpu" or "gpu")) return 2;
        string dir, runtime, output;
        try
        {
            dir = Path.GetFullPath(options["--dir"]);
            runtime = Path.GetFullPath(options["--runtime"]);
            output = Path.GetFullPath(options["--out"]);
        }
        catch (Exception) { return 2; }
        using var log = new FileLog();
        var variant = options["--variant"] == "cpu" ? VoiceVariant.Cpu : VoiceVariant.Gpu;
        var alreadyInstalled = false;
        VoiceCheckResult result;
        try
        {
            var catalog = VoiceCatalog.LoadEmbedded();
            var installer = new VoiceInstaller(catalog, new VoiceDownloader(VoiceHttp.Client, TimeProvider.System), VoiceForm.FreeBytes);
            alreadyInstalled = await installer.IsInstalledAsync(variant, dir, CancellationToken.None).ConfigureAwait(false);
            var installResult = "error";
            try
            {
                await installer.InstallAsync(variant, dir, runtime, null, CancellationToken.None).ConfigureAwait(false);
                installResult = "ok";
            }
            catch (VoiceInstallException error) { installResult = error.Failure.ToString(); throw; }
            finally { log.Info($"Voice install: variant={variant}; result={installResult}."); }
            var dictation = new DictationSettings();
            result = await VoiceVerifier.VerifyAsync(new WhisperSettings { Dir = dir },
                variant == VoiceVariant.Cpu ? WhisperMode.Cpu : WhisperMode.Gpu, dictation.Language, dictation.Prompt,
                2, CancellationToken.None, variant.ToString(), log).ConfigureAwait(false);
        }
        catch (Exception error) { result = new(false, "", 0, 0, null, null, error.Message); }
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                ok = result.Ok, alreadyInstalled, text = result.Text, startSeconds = result.StartSeconds,
                firstSeconds = result.FirstSeconds, secondSeconds = result.SecondSeconds,
                threads = result.Threads, error = result.Error
            }, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(output, json, new System.Text.UTF8Encoding(false)).ConfigureAwait(false);
        }
        catch (Exception error) { log.Error("Voice check report failed", error); return 1; }
        return result.Ok ? 0 : 1;
    }

}
