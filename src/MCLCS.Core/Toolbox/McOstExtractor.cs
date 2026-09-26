using System.Text.Json;
using MCLCS.Core.Utils;

namespace MCLCS.Core.Toolbox;

/// <summary>从 .minecraft/assets 提取的 MC 原声曲目（已映射到 objects/&lt;hash&gt; 实体文件）。</summary>
public class McOstTrack
{
    public string Title { get; init; } = "";
    public string Category { get; init; } = "";
    public string FilePath { get; init; } = "";
    public string Hash { get; init; } = "";

    /// <summary>转为统一播放列表曲目（Artist 标注为 Minecraft 原声）。</summary>
    public Track ToTrack() => new()
    {
        Path = FilePath,
        Title = Title,
        Artist = Category == "records" ? "Minecraft 唱片" : "C418 / Lena Raine"
    };
}

/// <summary>MC 原声按分类分组的视图模型。</summary>
public class McOstGroup
{
    public string Category { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public List<McOstTrack> Tracks { get; init; } = new();
}

/// <summary>
/// MC 原声提取：读取 <c>assets/indexes/&lt;version&gt;.json</c>，把
/// <c>minecraft/sounds/music</c>、<c>records</c>、<c>ambient</c> 等分类下的音频
/// 映射到 <c>assets/objects/&lt;hash前2位&gt;/&lt;hash&gt;</c> 实体文件，供播放器直接使用。
/// <para>注意：MC 原声为 .ogg 格式，依赖系统解码器（Windows Media Foundation 默认不含 OGG，
/// 用户机器若缺解码器需自行安装；提取逻辑本身不依赖解码）。</para>
/// </summary>
public static class McOstExtractor
{
    private static readonly HashSet<string> MusicPrefixes = new()
    {
        "minecraft/sounds/music/",
        "minecraft/sounds/records/",
        "minecraft/sounds/jukebox/",
        "minecraft/sounds/ambient/"
    };

    /// <summary>按相对路径归类（music / records / ambient / other）。</summary>
    private static string Classify(string path)
    {
        if (path.Contains("sounds/records/") || path.Contains("sounds/jukebox/")) return "records";
        if (path.Contains("sounds/music/")) return "music";
        if (path.Contains("sounds/ambient/")) return "ambient";
        return "other";
    }

    private static readonly Dictionary<string, string> CategoryDisplay = new()
    {
        ["music"] = "背景音乐",
        ["records"] = "唱片",
        ["ambient"] = "环境音",
        ["other"] = "其它"
    };

    /// <summary>记一次扫描的诊断信息，便于界面提示"为什么扫不到"（bug #10）。</summary>
    public static string LastDiagnostics { get; private set; } = "";

    /// <summary>
    /// 扫描 MC 原声。优先使用 <paramref name="versionId"/> 对应的索引；
    /// 不传则按版本号从新到旧依次尝试，直到找到含可播放音频的索引（bug #10：
    /// 此前按文件名字典序取"最大"，会选中 "8.json" 而不是 "1.21.json"，导致扫描结果为空）。
    /// 仅返回文件实际存在的曲目。
    /// </summary>
    public static List<McOstGroup> Scan(string gameRoot, string? versionId = null)
    {
        var groups = new List<McOstGroup>();
        LastDiagnostics = "";
        try
        {
            var indexDir = PathEx.AssetsIndexDir(gameRoot);
            if (!Directory.Exists(indexDir))
            {
                LastDiagnostics = "未找到 assets/indexes 目录（该游戏目录尚未下载资源索引）";
                return groups;
            }

            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(versionId))
            {
                candidates.Add(Path.Combine(indexDir, versionId + ".json"));
            }
            else
            {
                // 按版本号降序：1.21 > 1.20 > 1.9（字典序会把 8 排在 17 之后，不能直接用）
                candidates.AddRange(Directory.GetFiles(indexDir, "*.json")
                    .OrderByDescending(f => VersionRank(Path.GetFileNameWithoutExtension(f)))
                    .ThenByDescending(f => Path.GetFileNameWithoutExtension(f), StringComparer.OrdinalIgnoreCase));
            }

            var foundAny = false;
            foreach (var candidate in candidates)
            {
                if (!File.Exists(candidate)) continue;
                foundAny = true;
                var parsed = ScanIndex(gameRoot, candidate, out var hasAudio);
                if (parsed.Count > 0) return parsed;
                if (hasAudio)
                    // 索引里有音频条目，但实体文件缺失 → 资源未下载，换个版本索引通常也一样
                    LastDiagnostics = $"索引 {Path.GetFileName(candidate)} 内含音频条目，但 assets/objects 下缺少对应文件（资源未下载完整）";
            }

            if (!foundAny)
                LastDiagnostics = "assets/indexes 下没有找到任何版本索引文件";
            else if (groups.Count == 0 && LastDiagnostics.Length == 0)
                LastDiagnostics = "索引中未找到可播放的音频条目";
        }
        catch (Exception ex)
        {
            LastDiagnostics = $"扫描失败：{ex.Message}";
        }
        return groups;
    }

    /// <summary>把版本名（如 1.21.4 / 24w14a / 1.20）折算为可比较的排序权重。</summary>
    private static long VersionRank(string name)
    {
        // 快照形如 24w14a：年份 + 周次，量级远小于正式版主版本，统一放大后比较
        var m = System.Text.RegularExpressions.Regex.Match(name, @"^(\d+)w(\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success)
            return long.Parse(m.Groups[1].Value) * 1000L + long.Parse(m.Groups[2].Value);

        var parts = name.Split('.');
        long rank = 0;
        for (var i = 0; i < 3; i++)
        {
            var seg = i < parts.Length ? parts[i] : "0";
            var digits = new string(seg.TakeWhile(char.IsDigit).ToArray());
            if (digits.Length == 0) break;
            rank = rank * 1000 + long.Parse(digits);
        }
        return rank * 1000_000L; // 正式版整体排在快照之后
    }

    /// <summary>解析单个索引文件，返回分组结果；<paramref name="hasAudio"/> 表示索引中是否存在音频条目。</summary>
    private static List<McOstGroup> ScanIndex(string gameRoot, string indexFile, out bool hasAudio)
    {
        var groups = new List<McOstGroup>();
        hasAudio = false;
        using var doc = JsonDocument.Parse(File.ReadAllText(indexFile));
        if (!doc.RootElement.TryGetProperty("objects", out var objects)
            || objects.ValueKind != JsonValueKind.Object) return groups;

        var byCat = new Dictionary<string, List<McOstTrack>>();
        foreach (var prop in objects.EnumerateObject())
        {
            var key = prop.Name;
            if (!MusicPrefixes.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
            if (!key.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
                && !key.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
                && !key.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) continue;

            hasAudio = true;

            if (!prop.Value.TryGetProperty("hash", out var hashEl) || hashEl.ValueKind != JsonValueKind.String) continue;
            var hash = hashEl.GetString()!;
            if (hash.Length < 3) continue;

            var filePath = Path.Combine(PathEx.AssetsObjectsDir(gameRoot), hash[..2], hash);
            if (!File.Exists(filePath)) continue;

            var cat = Classify(key);
            var track = new McOstTrack
            {
                Title = FriendlyTitle(key, cat),
                Category = cat,
                FilePath = filePath,
                Hash = hash
            };
            if (!byCat.TryGetValue(cat, out var list))
                byCat[cat] = list = new List<McOstTrack>();
            list.Add(track);
        }

        foreach (var cat in new[] { "music", "records", "ambient", "other" })
        {
            if (!byCat.TryGetValue(cat, out var tracks) || tracks.Count == 0) continue;
            groups.Add(new McOstGroup
            {
                Category = cat,
                DisplayName = CategoryDisplay.GetValueOrDefault(cat, cat),
                Tracks = tracks.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase).ToList()
            });
        }
        return groups;
    }


    /// <summary>把 assets 路径文件名的友好中文标题（唱片加「唱片」前缀）。</summary>
    private static string FriendlyTitle(string path, string category)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        name = name.Replace('_', ' ');
        if (category == "records")
            return "唱片 " + char.ToUpperInvariant(name[0]) + name[1..];
        return char.ToUpperInvariant(name[0]) + name[1..];
    }
}
