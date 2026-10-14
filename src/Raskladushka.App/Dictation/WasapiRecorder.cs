using System.Runtime.InteropServices;
using Raskladushka.Core.Dictation;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Raskladushka.App.Dictation;

public sealed class WasapiRecorder(Func<string> microphone) : IAudioRecorder, IDisposable
{
    private readonly object _gate = new();
    private readonly object _audioGate = new();
    private readonly Func<string> _microphone = microphone;
    private WasapiCapture? _capture;
    private MMDevice? _device;
    private MemoryStream? _audio;
    private TaskCompletionSource<Exception?>? _stopped;

    public void Start()
    {
        lock (_gate)
        {
            if (_capture is not null) throw new InvalidOperationException("Запись уже начата.");
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var name = _microphone();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                    {
                        if (_device is null && device.FriendlyName.Contains(name, StringComparison.OrdinalIgnoreCase))
                            _device = device;
                        else device.Dispose();
                    }
                }
                _device ??= enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
                var audio = new MemoryStream();
                lock (_audioGate) _audio = audio;
                var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _stopped = stopped;
                _capture = new WasapiCapture(_device) { ShareMode = AudioClientShareMode.Shared };
                _capture.DataAvailable += (_, e) =>
                {
                    lock (_audioGate)
                        if (ReferenceEquals(_audio, audio)) audio.Write(e.Buffer, 0, e.BytesRecorded);
                };
                _capture.RecordingStopped += (_, e) => stopped.TrySetResult(e.Exception);
                _capture.StartRecording();
            }
            catch (Exception error)
            {
                Release();
                switch (error.HResult)
                {
                    case unchecked((int)0x80070490): throw new MicrophoneException(MicrophoneProblem.NotFound);
                    case unchecked((int)0x80070005): throw new MicrophoneException(MicrophoneProblem.AccessDenied);
                    case unchecked((int)0x8889000A): throw new MicrophoneException(MicrophoneProblem.Busy);
                }
                throw;
            }
        }
    }

    public AudioClip Stop()
    {
        lock (_gate)
        {
            if (_capture is null) throw new InvalidOperationException("Запись не начата.");
            try
            {
                _capture.StopRecording();
                // Capture callbacks must not need the lock held while waiting for them.
                if (!_stopped!.Task.Wait(TimeSpan.FromSeconds(2))) throw new TimeoutException("Микрофон недоступен.");
                if (_stopped.Task.Result is not null) throw new InvalidOperationException("Микрофон недоступен.");
                byte[] recorded;
                lock (_audioGate) recorded = _audio!.ToArray();
                using var source = new RawSourceWaveStream(new MemoryStream(recorded), _capture.WaveFormat);
                ISampleProvider samples = source.ToSampleProvider();
                if (samples.WaveFormat.Channels != 1) samples = new MonoProvider(samples);
                if (samples.WaveFormat.SampleRate != 16000) samples = new WdlResamplingSampleProvider(samples, 16000);
                var pcm = new SampleToWaveProvider16(samples);
                using var data = new MemoryStream();
                var buffer = new byte[32000];
                int read;
                while ((read = pcm.Read(buffer, 0, buffer.Length)) > 0) data.Write(buffer, 0, read);
                var bytes = data.ToArray();
                var rms = AudioStats.Rms(MemoryMarshal.Cast<byte, short>(bytes));
                using var wav = new MemoryStream();
                using (var writer = new WaveFileWriter(new NAudio.Utils.IgnoreDisposeStream(wav), new WaveFormat(16000, 16, 1)))
                    writer.Write(bytes, 0, bytes.Length);
                return new AudioClip(wav.ToArray(), TimeSpan.FromSeconds(bytes.Length / 32000.0), rms);
            }
            finally { Release(); }
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            try
            {
                if (_capture is null) return;
                _capture.StopRecording();
                _stopped?.Task.Wait(TimeSpan.FromSeconds(2));
            }
            finally { Release(); }
        }
    }
    public void Dispose() => Cancel();

    private void Release()
    {
        try { _capture?.Dispose(); }
        finally
        {
            _capture = null;
            _stopped = null;
            try { _device?.Dispose(); }
            finally
            {
                _device = null;
                lock (_audioGate)
                {
                    _audio?.Dispose();
                    _audio = null;
                }
            }
        }
    }

    private sealed class MonoProvider(ISampleProvider source) : ISampleProvider
    {
        private readonly ISampleProvider _source = source;
        private float[] _buffer = [];
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        public int Read(float[] buffer, int offset, int count)
        {
            var channels = _source.WaveFormat.Channels;
            if (_buffer.Length < count * channels) _buffer = new float[count * channels];
            var frames = _source.Read(_buffer, 0, count * channels) / channels;
            for (var frame = 0; frame < frames; frame++)
            {
                float sum = 0;
                for (var channel = 0; channel < channels; channel++) sum += _buffer[frame * channels + channel];
                buffer[offset + frame] = sum / channels;
            }
            return frames;
        }
    }
}
