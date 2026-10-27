using System.Text;
using Concentus.Oggfile;
using Concentus.Structs;
using Raskladushka.Core.Dictation;

namespace Raskladushka.Core.Tests;

public sealed class OpusOggEncoderTests(ITestOutputHelper output)
{
    internal static AudioClip Clip(double seconds = 0.1, int channels = 1, int rate = 16000, int bits = 16,
        bool extraChunk = false)
    {
        var count = (int)(seconds * rate);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write("RIFF"u8); writer.Write(0); writer.Write("WAVE"u8);
        if (extraChunk) { writer.Write("JUNK"u8); writer.Write(3); writer.Write(new byte[4]); }
        writer.Write("fmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)channels);
        writer.Write(rate); writer.Write(rate * channels * bits / 8);
        writer.Write((short)(channels * bits / 8)); writer.Write((short)bits);
        writer.Write("data"u8); writer.Write(count * channels * bits / 8);
        var random = new Random(42);
        for (var i = 0; i < count; i++)
        {
            var sample = (short)(10000 * Math.Sin(2 * Math.PI * 440 * i / rate) + random.Next(-500, 501));
            for (var channel = 0; channel < channels; channel++)
                if (bits == 16) writer.Write(sample); else writer.Write((byte)128);
        }
        stream.Position = 4;
        writer.Write((int)stream.Length - 8);
        return new(stream.ToArray(), TimeSpan.FromSeconds(seconds), 0.2);
    }

    [Fact]
    public void E1_E2_TwentySecondsHasOggHeadersSizeDurationAndSignal()
    {
        var clip = Clip(20);
        var ogg = OpusOggEncoder.Encode(clip);
        output.WriteLine($"Е1: Ogg = {ogg.Length} bytes");
        Assert.True(ogg.AsSpan(0, 4).SequenceEqual("OggS"u8));
        Assert.Contains("OpusHead", Encoding.ASCII.GetString(ogg));
        Assert.InRange(ogg.Length, 5 * 1024, (int)(24000 / 8 * 20 * 1.15 + 2048));
        using var stream = new MemoryStream(ogg);
        var reader = new OpusOggReadStream(ManagedDecoder(), stream);
        var decoded = new List<short>();
        while (reader.HasNextPacket)
        {
            var packet = reader.DecodeNextPacket();
            if (packet is not null) decoded.AddRange(packet);
        }
        Assert.Null(reader.LastError);
        Assert.InRange(decoded.Count / 16000.0, 19.94, 20.06);
        var rms = Math.Sqrt(decoded.Average(s => (double)s * s));
        var original = new short[(clip.Wav.Length - 44) / 2];
        Buffer.BlockCopy(clip.Wav, 44, original, 0, original.Length * 2);
        var originalRms = Math.Sqrt(original.Average(s => (double)s * s));
        Assert.InRange(rms / originalRms, 0.5, 1.5);
        output.WriteLine($"Е2: duration = {decoded.Count / 16000.0:F3} s; RMS ratio = {rms / originalRms:F3}");
    }

    [Fact]
    public void E3_TwoMinutesFitsUploadBudget()
    {
        var ogg = OpusOggEncoder.Encode(Clip(120));
        output.WriteLine($"Е3: Ogg = {ogg.Length} bytes");
        Assert.InRange(ogg.Length, 1, 400000);
    }

    [Theory]
    [InlineData(1, 2, 16000, 16)]
    [InlineData(1, 1, 44100, 16)]
    [InlineData(1, 1, 16000, 8)]
    [InlineData(0, 1, 16000, 16)]
    public void E4_InvalidFormatIsRejected(double seconds, int channels, int rate, int bits) =>
        Assert.Throws<InvalidDataException>(() => OpusOggEncoder.Encode(Clip(seconds, channels, rate, bits)));

    [Fact]
    public void ExtraChunksAreSkippedAndPartialFrameIsPadded()
    {
        var ogg = OpusOggEncoder.Encode(Clip(0.027, extraChunk: true));
        var reader = new OpusOggReadStream(ManagedDecoder(), new MemoryStream(ogg));
        var count = 0;
        while (reader.HasNextPacket) count += reader.DecodeNextPacket()?.Length ?? 0;
        Assert.InRange(count / 16000.0, 0.027, 0.087);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(43)]
    public void TruncatedWavIsRejected(int length)
    {
        var clip = Clip();
        Assert.Throws<InvalidDataException>(() => OpusOggEncoder.Encode(clip with { Wav = clip.Wav[..length] }));
    }

    // Keep the round trip entirely managed, just like the production encoder.
#pragma warning disable CS0618
    private static OpusDecoder ManagedDecoder() => new(16000, 1);
#pragma warning restore CS0618
}
