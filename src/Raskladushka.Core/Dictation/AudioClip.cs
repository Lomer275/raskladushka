namespace Raskladushka.Core.Dictation;

public sealed record AudioClip(byte[] Wav, TimeSpan Duration, double Rms);
