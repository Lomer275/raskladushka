using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using NAudio.Wave;
using Raskladushka.App.Dictation;
using Raskladushka.Core.Config;
using Raskladushka.Core.Dictation;

namespace Raskladushka.App.Shell;

public sealed record VoiceCheckResult(bool Ok, string Text, double StartSeconds, double FirstSeconds,
    double? SecondSeconds, int? Threads, string? Error);

public static class VoiceVerifier
{
    public static AudioClip LoadSelftestClip()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "selftest.wav");
        if (!File.Exists(path)) throw new InvalidDataException($"Не найден {path}");
        var wav = File.ReadAllBytes(path);
        try
        {
            using var reader = new WaveFileReader(new MemoryStream(wav));
            if (reader.WaveFormat.SampleRate != 16000 || reader.WaveFormat.Channels != 1 ||
                reader.WaveFormat.BitsPerSample != 16 || reader.WaveFormat.Encoding != WaveFormatEncoding.Pcm)
                throw new InvalidDataException("Тестовая запись должна быть WAV 16 кГц моно PCM16.");
            using var pcm = new MemoryStream();
            reader.CopyTo(pcm);
            return new AudioClip(wav, reader.TotalTime, AudioStats.Rms(MemoryMarshal.Cast<byte, short>(pcm.ToArray())));
        }
        catch (Exception error) when (error is FormatException or ArgumentException or InvalidDataException or EndOfStreamException)
        {
            throw new InvalidDataException("Тестовая запись должна быть WAV 16 кГц моно PCM16.", error);
        }
    }

    public static async Task<VoiceCheckResult> VerifyAsync(WhisperSettings settings, WhisperMode mode,
        string language, string prompt, int runs, CancellationToken ct)
    {
        using var log = new FileLog();
        return await VerifyAsync(settings, mode, language, prompt, runs, ct, "installed", log).ConfigureAwait(false);
    }

    internal static async Task<VoiceCheckResult> VerifyAsync(WhisperSettings settings, WhisperMode mode,
        string language, string prompt, int runs, CancellationToken ct, string variant, FileLog log)
    {
        double start = 0, first = 0;
        double? second = null;
        var text = "";
        var ok = false;
        string? error = null;
        var errorType = "none";
        using var whisper = new WhisperServerHost(() => settings, () => language);
        var watch = new Stopwatch();
        var phase = -1;
        try
        {
            if (runs < 1) throw new ArgumentOutOfRangeException(nameof(runs));
            var clip = LoadSelftestClip();
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct);
            startup.CancelAfter(DictationMachine.StartTimeout);
            phase = 0;
            watch.Restart();
            try { await whisper.StartAsync(mode, startup.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && startup.IsCancellationRequested)
            { throw new TimeoutException("Не удалось запустить Whisper за отведённое время."); }
            start = watch.Elapsed.TotalSeconds;
            startup.CancelAfter(Timeout.InfiniteTimeSpan);
            ok = true;
            for (var i = 0; i < runs; i++)
            {
                phase = i + 1;
                watch.Restart();
                ct.ThrowIfCancellationRequested();
                text = await whisper.TranscribeAsync(clip, language, prompt, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (i == 0) first = watch.Elapsed.TotalSeconds;
                if (i == 1) second = watch.Elapsed.TotalSeconds;
                ok &= text.Contains("голосовой ввод", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex)
        {
            ok = false;
            errorType = ct.IsCancellationRequested ? nameof(OperationCanceledException) : ex.GetType().Name;
            if (phase == 0) start = watch.Elapsed.TotalSeconds;
            if (phase == 1) first = watch.Elapsed.TotalSeconds;
            if (phase == 2) second = watch.Elapsed.TotalSeconds;
            ct.ThrowIfCancellationRequested();
            error = ex.Message;
        }
        finally
        {
            whisper.Stop();
            log.Info($"Voice check: variant={variant}; mode={mode}; ok={ok.ToString().ToLowerInvariant()}; " +
                $"seconds={(start + first).ToString("F1", CultureInfo.InvariantCulture)}; error={errorType}.");
        }
        return new(ok, text, start, first, second,
            mode == WhisperMode.Cpu ? WhisperLayout.CpuThreads(settings, Environment.ProcessorCount) : null, error);
    }
}
