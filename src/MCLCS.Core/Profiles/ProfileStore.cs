using System.IO;
using System.Text.Json;

namespace MCLCS.Core.Profiles;

/// <summary>配置读写（profiles.json）。</summary>
public static class ProfileStore
{
    private static string ProfilePath(string gameRoot)
        => Path.Combine(gameRoot, "mclcs_profiles.json");

    public static LauncherProfile Load(string gameRoot)
    {
        var path = ProfilePath(gameRoot);
        if (!File.Exists(path)) return new LauncherProfile { GameRoot = gameRoot };
        try
        {
            var json = File.ReadAllText(path);
            var p = JsonSerializer.Deserialize<LauncherProfile>(json) ?? new LauncherProfile();
            // 音乐客户端设置容错：配置可能被手改/来自旧版本，宽限期钳制到 0–30、
            // 非法歌词固定方式回退默认，避免脏值渗进业务逻辑（对齐 WPF）。
            p.MusicClient = (p.MusicClient ?? new MusicClientPrefs()).Normalized();
            p.MusicApi = (p.MusicApi ?? new MusicApiPrefs()).Normalized();
            return p;
        }
        catch
        {
            return new LauncherProfile { GameRoot = gameRoot };
        }
    }

    public static void Save(LauncherProfile profile)
    {
        Directory.CreateDirectory(profile.GameRoot);
        var path = ProfilePath(profile.GameRoot);
        var json = JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// 将存档配置文件从旧目录迁移到新目录（对齐 WPF 改游戏目录的语义）。
    /// <list type="bullet">
    ///   <item>新目录还不存在配置时：复制旧目录的 <c>mclcs_profiles.json</c> 到新目录，再删除旧副本；</item>
    ///   <item>新目录已存在配置时（如本次保存刚写入的新鲜副本）：保留新目录、删除旧副本，避免遗留孤儿文件；</item>
    ///   <item>旧目录本就无配置时：直接返回，无需迁移。</item>
    /// </list>
    /// 这样「设置 → 启动」改目录后，profile 跟随目录搬家，重启也不会在新目录找不到存档而清空设置。
    /// </summary>
    public static void Migrate(string oldRoot, string newRoot)
    {
        if (string.Equals(oldRoot, newRoot, StringComparison.OrdinalIgnoreCase)) return;
        var oldPath = ProfilePath(oldRoot);
        if (!File.Exists(oldPath)) return;

        try
        {
            var newPath = ProfilePath(newRoot);
            if (!File.Exists(newPath))
            {
                Directory.CreateDirectory(newRoot);
                File.Copy(oldPath, newPath);
            }
            File.Delete(oldPath);   // 清掉旧目录副本，避免遗留孤儿文件
        }
        catch
        {
            // 任何失败都不影响新目录已生效的存档
        }
    }
}
