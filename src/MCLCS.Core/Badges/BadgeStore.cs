using System.Text.Json;

using MCLCS.Core.Utils;

namespace MCLCS.Core.Badges;

/// <summary>
/// 勋章存档读写（<c>&lt;游戏目录&gt;/mclcs_badges.json</c>）。
/// 清单 #49：DeviceId 在首次读取时生成并写回，之后保持不变。
/// </summary>
public static class BadgeStore
{
    private static readonly object Sync = new();
    private static BadgeProfile? _cache;
    private static string _cachedRoot = "";

    public static string FilePath(string gameRoot) => Path.Combine(gameRoot, "mclcs_badges.json");

    /// <summary>读取存档；不存在或解析失败时返回带新 DeviceId 的空存档。</summary>
    public static BadgeProfile Load(string gameRoot)
    {
        var path = FilePath(gameRoot);
        BadgeProfile? p = null;
        if (File.Exists(path))
        {
            try
            {
                p = JsonSerializer.Deserialize<BadgeProfile>(File.ReadAllText(path));
            }
            catch
            {
                p = null;
            }
        }

        p ??= new BadgeProfile();
        p.Badges ??= new List<BadgeRecord>();
        if (string.IsNullOrWhiteSpace(p.DeviceId))
        {
            p.DeviceId = Guid.NewGuid().ToString("N");
            Save(gameRoot, p);   // 立即落盘，避免每次启动都换一个 DeviceId
        }

        return p;
    }

    public static void Save(string gameRoot, BadgeProfile profile)
    {
        try
        {
            Directory.CreateDirectory(gameRoot);
            File.WriteAllText(FilePath(gameRoot),
                JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 勋章存档写失败不应影响启动器主流程
        }
    }

    /// <summary>
    /// 进程内缓存的存档实例（默认游戏目录）。改动后调用 <see cref="Flush"/> 落盘。
    /// </summary>
    public static BadgeProfile Current
    {
        get
        {
            lock (Sync)
            {
                var root = GameConstants.DefaultGameRoot;
                if (_cache is null || !string.Equals(_cachedRoot, root, StringComparison.OrdinalIgnoreCase))
                {
                    _cache = Load(root);
                    _cachedRoot = root;
                }

                return _cache;
            }
        }
    }

    /// <summary>本机设备标识（不存在时生成并持久化）。</summary>
    public static string DeviceId => Current.DeviceId;

    /// <summary>把缓存中的存档写回磁盘。</summary>
    public static void Flush()
    {
        lock (Sync)
        {
            if (_cache is null) return;
            Save(_cachedRoot.Length == 0 ? GameConstants.DefaultGameRoot : _cachedRoot, _cache);
        }
    }
}
