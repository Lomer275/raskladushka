using System.Text.Json;

namespace Raskladushka.Core.Voice;

public sealed class VoiceCatalog
{
    private readonly Dictionary<VoiceVariant, IReadOnlyList<VoicePackage>> variants;

    private VoiceCatalog(string release, Dictionary<VoiceVariant, IReadOnlyList<VoicePackage>> variants)
    {
        WhisperRelease = release;
        this.variants = variants;
    }

    public string WhisperRelease { get; }
    public IReadOnlyList<VoicePackage> Packages(VoiceVariant variant) => variants[variant];
    public long DownloadBytes(VoiceVariant variant) => Packages(variant).Sum(p => p.Size);
    public long DiskBytes(VoiceVariant variant) => Packages(variant).Sum(p =>
        p.Kind == PackageKind.Build ? p.UnpackedSize : p.Size);

    public static VoiceCatalog LoadEmbedded()
    {
        using var stream = typeof(VoiceCatalog).Assembly.GetManifestResourceStream("voice-catalog.json")
            ?? throw new FormatException("Не найден каталог голоса.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    public static VoiceCatalog Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var release = Text(root.GetProperty("whisperRelease"));
            var packages = new Dictionary<string, VoicePackage>(StringComparer.Ordinal);
            foreach (var item in root.GetProperty("packages").EnumerateArray())
            {
                var id = Text(item.GetProperty("id"));
                var kind = Text(item.GetProperty("kind")) switch
                {
                    "build" => PackageKind.Build,
                    "model" => PackageKind.Model,
                    _ => throw new FormatException("Неизвестный вид пакета.")
                };
                var target = Name(item.GetProperty("target"));
                var file = Name(item.GetProperty("file"));
                if (!Uri.TryCreate(Text(item.GetProperty("url")), UriKind.Absolute, out var url) ||
                    url.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(url.Host))
                    throw new FormatException("Некорректный адрес пакета.");
                var size = item.GetProperty("size").GetInt64();
                var unpacked = item.GetProperty("unpackedSize").GetInt64();
                var sha = Text(item.GetProperty("sha256"));
                if (size <= 0 || unpacked < 0 || (kind == PackageKind.Build && unpacked == 0) ||
                    sha.Length != 64 || !sha.All(Uri.IsHexDigit))
                    throw new FormatException("Некорректный размер или сумма пакета.");
                if (!packages.TryAdd(id, new(id, kind, target, file, url, size, unpacked, sha.ToLowerInvariant())))
                    throw new FormatException("Повтор идентификатора пакета.");
            }
            var variants = new Dictionary<VoiceVariant, IReadOnlyList<VoicePackage>>();
            foreach (var (variant, name) in new[] { (VoiceVariant.Gpu, "gpu"), (VoiceVariant.Cpu, "cpu"),
                         (VoiceVariant.CpuModel, "cpuModel") })
            {
                var list = new List<VoicePackage>();
                foreach (var id in root.GetProperty("variants").GetProperty(name).EnumerateArray())
                {
                    if (!packages.TryGetValue(Text(id), out var package))
                        throw new FormatException("Вариант ссылается на неизвестный пакет.");
                    list.Add(package);
                }
                variants.Add(variant, list.AsReadOnly());
            }
            return new(release, variants);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException
                                   or ArgumentException or OverflowException)
        {
            throw new FormatException("Некорректный каталог голоса.", ex);
        }
    }

    private static string Text(JsonElement element) =>
        !string.IsNullOrWhiteSpace(element.GetString()) ? element.GetString()! :
            throw new FormatException("Пустое поле каталога.");

    private static string Name(JsonElement element)
    {
        var value = Text(element);
        if (value.IndexOfAny(['\\', '/', ':']) >= 0 || value.Contains("..") || value == ".")
            throw new FormatException("Некорректное имя файла или папки.");
        return value;
    }
}
