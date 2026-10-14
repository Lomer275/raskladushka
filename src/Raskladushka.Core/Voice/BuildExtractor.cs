using System.IO.Compression;

namespace Raskladushka.Core.Voice;

public static class BuildExtractor
{
    public static void Extract(string zipPath, string stagingDir, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(stagingDir);
        var archiveName = Path.GetFileName(zipPath);
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var name = entry.FullName.Replace('\\', '/').Split('/')[^1];
                if (!name.Equals("whisper-server.exe", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                // A drive prefix or alternate data stream must not escape the flattened destination on Windows.
                if (name.Contains(':'))
                    throw new VoiceInstallException(VoiceFailure.Extraction,
                        $"В архиве {archiveName} недопустимое имя файла {name}.");
                if (!names.Add(name))
                    throw new VoiceInstallException(VoiceFailure.Extraction,
                        $"В архиве {archiveName} два файла {name}.");
                entry.ExtractToFile(Path.Combine(stagingDir, name));
            }
            if (!names.Contains("whisper-server.exe"))
                throw new VoiceInstallException(VoiceFailure.Extraction,
                    $"В архиве {archiveName} нет whisper-server.exe.");
        }
        catch (InvalidDataException)
        {
            throw new VoiceInstallException(VoiceFailure.Extraction, $"Не удалось распаковать архив {archiveName}.");
        }
    }
}
