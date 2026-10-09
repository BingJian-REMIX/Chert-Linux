using System.Text.Json;
using System.Text.Json.Nodes;

namespace MCLCS.Core.Profiles;

/// <summary>
/// 启动配置迁移：把**旧版本**写出的 <c>mclcs_profiles.json</c> 升级到当前版本的结构。
///
/// <para><b>触发条件</b>（三者同时满足，避免初次使用误触发）：</para>
/// <list type="number">
///   <item>游戏目录里**已存在**旧版配置文件 —— 初次使用没有这个文件，直接跳过；</item>
///   <item>配置里的 <c>configVersion</c> 缺失或**小于** <see cref="CurrentConfigVersion"/>；</item>
///   <item>尚未标记为已迁移到当前版本（由版本号本身保证，天然幂等）。</item>
/// </list>
///
/// <para><b>为什么需要</b>：自动更新只替换程序集，用户配置仍留在游戏目录里。
/// 旧配置缺新字段时，<c>System.Text.Json</c> 反序列化会给字段类型的默认值
/// （<c>int</c> 为 0、<c>string</c> 为 null、枚举为 0）—— 这会让「未设置」与「设为 0」无法区分，
/// 迁移要显式补上与 <see cref="LauncherProfile"/> 一致的默认值。</para>
///
/// <para><b>键名约定</b>：<see cref="LauncherProfile"/> 全部 65 个字段都带
/// <c>[JsonPropertyName]</c> 小驼峰映射，<c>ProfileStore</c> 也未设
/// <c>PropertyNamingPolicy</c>，因此本类一律使用 **JSON 键名**（小驼峰）而非属性名。</para>
///
/// <para><b>安全策略</b>：改动前把原文件备份为 <c>mclcs_profiles.json.bak</c>；
/// 任何一步失败都还原原文件、记录 <see cref="LastError"/>，**绝不阻断启动**。</para>
/// </summary>
public static class ConfigMigrator
{
    /// <summary>当前配置结构版本。升级配置结构时 +1，并在 <see cref="ApplyUpgrade"/> 加对应分支。</summary>
    public const int CurrentConfigVersion = 2;

    /// <summary>版本标记的 JSON 键名（小驼峰，与其他字段一致）。</summary>
    public const string VersionKey = "configVersion";

    private const string ProfileFileName = "mclcs_profiles.json";
    private const string BackupSuffix = ".bak";

    /// <summary>迁移结果分类。</summary>
    public enum Outcome
    {
        /// <summary>无需迁移：初次使用、已是当前版本、或配置不属于本启动器。</summary>
        Skipped,
        /// <summary>迁移成功且补齐了新字段。</summary>
        Migrated,
        /// <summary>仅补写了版本标记，内容无变化。</summary>
        VersionedOnly,
        /// <summary>失败（已还原原文件，调用方应 Toast 提示但不得中断启动）。</summary>
        Failed
    }

    /// <summary>迁移结果详情。</summary>
    public sealed class Result
    {
        public Outcome Outcome { get; init; }
        public int FromVersion { get; init; }
        public int ToVersion { get; init; }
        /// <summary>本次补齐的字段说明，用于向用户解释「迁移了什么」。</summary>
        public List<string> Changes { get; } = new();
        public string? Error { get; init; }
    }
    public static string? LastError { get; private set; }

    private static string ProfilePath(string gameRoot) => Path.Combine(gameRoot, ProfileFileName);

    /// <summary>
    /// 执行迁移。<paramref name="gameRoot"/> 为当前生效的游戏目录。
    /// </summary>
    public static Result Run(string gameRoot)
    {
        LastError = null;
        if (string.IsNullOrWhiteSpace(gameRoot)) return new Result { Outcome = Outcome.Skipped };

        var path = ProfilePath(gameRoot);

        // ① 初次使用：目录里没有配置文件 → 无需迁移
        if (!File.Exists(path)) return new Result { Outcome = Outcome.Skipped };

        JsonObject obj;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject parsed)
                return new Result { Outcome = Outcome.Skipped };
            obj = parsed;
        }
        catch (Exception ex)
        {
            // 配置损坏：ProfileStore.Load 另有兜底（返回默认配置），此处不重复打扰用户
            LastError = $"配置文件解析失败：{ex.Message}";
            return new Result { Outcome = Outcome.Failed, Error = LastError };
        }

        // ② 读版本号；缺失视为 1（最早的结构）
        var fromVersion = ReadVersion(obj);
        if (fromVersion >= CurrentConfigVersion)
            return new Result { Outcome = Outcome.Skipped, FromVersion = fromVersion, ToVersion = fromVersion };

        var changes = new List<string>();

        // ③ 备份（仅在真要改动前做）
        try
        {
            File.Copy(path, path + BackupSuffix, overwrite: true);
        }
        catch (Exception ex)
        {
            LastError = $"备份原配置失败：{ex.Message}";
            return new Result { Outcome = Outcome.Failed, FromVersion = fromVersion, Error = LastError };
        }

        // ④ 逐版本升级 + 写回
        try
        {
            for (var v = fromVersion + 1; v <= CurrentConfigVersion; v++)
                ApplyUpgrade(obj, v, changes);

            obj[VersionKey] = CurrentConfigVersion;

            File.WriteAllText(path,
                obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            var res = new Result
            {
                Outcome = changes.Count > 0 ? Outcome.Migrated : Outcome.VersionedOnly,
                FromVersion = fromVersion,
                ToVersion = CurrentConfigVersion
            };
            res.Changes.AddRange(changes);
            return res;
        }
        catch (Exception ex)
        {
            // 迁移写坏 → 用备份还原，保证用户配置不被破坏
            try
            {
                var bak = path + BackupSuffix;
                if (File.Exists(bak)) File.Copy(bak, path, overwrite: true);
            }
            catch { /* 还原也失败时保持原状，绝不阻断启动 */ }

            LastError = $"迁移失败（已还原原配置）：{ex.Message}";
            return new Result
            {
                Outcome = Outcome.Failed,
                FromVersion = fromVersion,
                ToVersion = CurrentConfigVersion,
                Error = LastError
            };
        }
    }

    private static int ReadVersion(JsonObject obj)
    {
        if (obj.TryGetPropertyValue(VersionKey, out var node) && node is JsonValue v)
        {
            try { return v.GetValue<int>(); } catch { /* 非数字视为缺失 */ }
        }
        return 1;
    }

    /// <summary>
    /// 单版本升级。<paramref name="toVersion"/> 是**目标**版本号。
    /// 一律用 JSON 键名（小驼峰）操作，与 <c>[JsonPropertyName]</c> 保持一致。
    /// </summary>
    private static void ApplyUpgrade(JsonObject obj, int toVersion, List<string> changes)
    {
        if (toVersion == 2)
        {
            // 清单 #67：全局下载限速，0 = 不限速。旧配置无此字段时补 0（等价于旧行为「不限速」）。
            EnsureDefault(obj, "downloadSpeedLimitKbps", 0, "下载限速（0 表示不限速）", changes);

            // 单任务失败自动重试次数，0 = 不重试。旧配置无此字段时补 0（保持旧行为）。
            EnsureDefault(obj, "downloadAutoRetryCount", 0, "下载失败重试次数（0 表示不重试）", changes);

            // 主题色：与 LauncherProfile.ThemeColor 的默认值保持一致。
            EnsureDefault(obj, "themeColor", "#3a7b4f", "主题色", changes);

            // 字体缩放：1.0 = 100%。
            EnsureDefault(obj, "fontScale", 1.0, "字体缩放", changes);
        }
    }

    /// <summary>字段缺失时补默认值并记录变更说明（已存在则不覆盖用户的值）。</summary>
    private static void EnsureDefault(JsonObject obj, string key, JsonNode value, string label, List<string> changes)
    {
        if (obj.ContainsKey(key)) return;
        obj[key] = value;
        changes.Add($"{label}（{key}）已补默认值");
    }
}
