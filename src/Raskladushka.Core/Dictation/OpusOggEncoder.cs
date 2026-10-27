using System.Buffers.Binary;
using Concentus.Enums;
using Concentus.Oggfile;
using Concentus.Structs;

namespace Raskladushka.Core.Dictation;

public static class OpusOggEncoder
{
    public static byte[] Encode(AudioClip clip)
    {
        var wav = clip.Wav.AsSpan();
        if (wav.Length < 12 || !wav[..4].SequenceEqual("RIFF"u8) || !wav.Slice(8, 4).SequenceEqual("WAVE"u8))
            throw InvalidWav();
        var riffLength = BinaryPrimitives.ReadUInt32LittleEndian(wav[4..]);
        if (riffLength < 4 || riffLength > wav.Length - 8) throw InvalidWav();
        var end = (int)riffLength + 8;
        var validFormat = false;
        ReadOnlySpan<byte> data = default;
        for (var offset = 12; offset < end;)
        {
            if (end - offset < 8) throw InvalidWav();
            var size = BinaryPrimitives.ReadUInt32LittleEndian(wav[(offset + 4)..]);
            if (size > end - offset - 8) throw InvalidWav();
            var chunk = wav.Slice(offset + 8, (int)size);
            if (wav.Slice(offset, 4).SequenceEqual("fmt "u8))
            {
                if (chunk.Length < 16 || BinaryPrimitives.ReadUInt16LittleEndian(chunk) != 1 ||
                    BinaryPrimitives.ReadUInt16LittleEndian(chunk[2..]) != 1 ||
                    BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]) != 16000 ||
                    BinaryPrimitives.ReadUInt32LittleEndian(chunk[8..]) != 32000 ||
                    BinaryPrimitives.ReadUInt16LittleEndian(chunk[12..]) != 2 ||
                    BinaryPrimitives.ReadUInt16LittleEndian(chunk[14..]) != 16) throw InvalidWav();
                validFormat = true;
            }
            else if (wav.Slice(offset, 4).SequenceEqual("data"u8)) data = chunk;
            var next = (long)offset + 8 + size + (size & 1);
            if (next > end) throw InvalidWav();
            offset = (int)next;
        }
        if (!validFormat || data.IsEmpty || data.Length % 2 != 0) throw InvalidWav();

        // Construct the managed codec directly to avoid native library discovery.
#pragma warning disable CS0618
        var encoder = new OpusEncoder(16000, 1, OpusApplication.OPUS_APPLICATION_VOIP) { Bitrate = 24000 };
#pragma warning restore CS0618
        using var output = new MemoryStream();
        var writer = new OpusOggWriteStream(encoder, output, new OpusTags(), inputSampleRate: 16000, leaveOpen: true);
        var frame = new short[320];
        for (var offset = 0; offset < data.Length; offset += 640)
        {
            Array.Clear(frame);
            var count = Math.Min(320, (data.Length - offset) / 2);
            for (var i = 0; i < count; i++) frame[i] = BinaryPrimitives.ReadInt16LittleEndian(data[(offset + i * 2)..]);
            writer.WriteSamples(frame, 0, frame.Length);
        }
        writer.Finish();
        return output.ToArray();
    }

    private static InvalidDataException InvalidWav() => new("Ожидалась непустая запись WAV PCM16, моно, 16 кГц.");
}
