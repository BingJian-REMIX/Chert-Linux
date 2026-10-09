using System.Text.Json;
using MCLCS.Core.Models;
using MCLCS.Core.Mods;
using MCLCS.Core.Utils;

namespace MCLCS.Core.Launcher;

/// <summary>缺失前置的种类。</summary>
public enum VersionPrerequisiteKind
{
    /// <summary><c>inheritsFrom</c> 链上缺失某个父版本（如 Forge 缺对应的原版）。</summary>
    InheritedVersion,

    /// <summary>版本自身的客户端 jar 缺失。</summary>
    ClientJar,

    /// <summary>某个 Mod 声明的强制前置依赖未安装。</summary>
    ModDependency
}

/// <summary>一条缺失前置记录。</summary>
public sealed class VersionPrerequisiteInfo
{
    public VersionPrerequisiteKind Kind { get; init; }

    /// <summary>机器可读标识（父版本 Id 或 Mod 依赖 Id）。</summary>
    public string Id { get; init; } = "";

    /// <summary>面向用户的展示文本。</summary>
    public string Display { get; init; } = "";

    public override string ToString() => Display;
}

/// <summary>
/// 版本「缺失前置」扫描器：判断某个已安装版本是否缺少它运行所需的依赖。
/// <para>
/// 覆盖两个维度：
/// <list type="bullet">
///   <item><b>原版继承链</b>——Forge / NeoForge 等版本依赖 <c>inheritsFrom</c> 指定的原版 json 与客户端 jar；</item>
///   <item><b>Mod 强制依赖</b>——Mod 元数据（fabric.mod.json / mods.toml）里 <c>depends</c> 声明但未安装的前置 Mod。</item>
/// </list>
/// 结果为列表页提供「缺失前置」标志，替代原先只在启动时弹窗提醒的做法
/// （弹窗仅在启动瞬间可见，且点掉后无从回溯）。
/// </para>
/// <para>全程本地 IO 与 jar 元数据解析，无网络访问；任何异常都被吞掉，绝不因扫描失败影响列表展示。</para>
/// </summary>
public static class VersionPrerequisiteScanner
{
    /// <summary>
    /// 扫描指定版本的缺失前置项。返回空列表表示前置齐全。
    /// </summary>
    /// <param name="gameRoot">游戏根目录（versions/ 的父目录）。</param>
    /// <param name="versionId">版本 Id。</param>
    /// <param name="effectiveGameDir">该版本的有效工作目录（版本隔离时取 versions/&lt;id&gt;），用于定位其 mods 目录。</param>
    public static List<VersionPrerequisiteInfo> Scan(string gameRoot, string versionId, string? effectiveGameDir)
    {
        var result = new List<VersionPrerequisiteInfo>();
        if (string.IsNullOrWhiteSpace(versionId) || string.IsNullOrWhiteSpace(gameRoot))
            return result;

        ScanVanillaChain(gameRoot, versionId, result);
        ScanModDependencies(effectiveGameDir, result);

        return result;
    }

    /// <summary>沿 inheritsFrom 向上检查每个父版本的 json 与 jar 是否齐备。</summary>
    private static void ScanVanillaChain(string gameRoot, string versionId, List<VersionPrerequisiteInfo> result)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { versionId };
        var current = versionId;

        for (var depth = 0; depth < 8 && !string.IsNullOrEmpty(current); depth++)
        {
            var jsonPath = PathEx.VersionJsonPath(gameRoot, current);
            if (!File.Exists(jsonPath))
            {
                // 自身没有 json 属于「版本不存在」，由上层负责，不算缺失前置；父版本缺失才是
                if (depth > 0)
                    result.Add(new VersionPrerequisiteInfo
                    {
                        Kind = VersionPrerequisiteKind.InheritedVersion,
                        Id = current,
                        Display = $"前置原版 {current}"
                    });
                return;
            }

            if (!File.Exists(PathEx.VersionJarPath(gameRoot, current)))
            {
                // 非继承的独立版本缺自己的 jar 才是「客户端 jar 缺失」；继承场景缺的是父 jar
                var kind = depth == 0
                    ? VersionPrerequisiteKind.ClientJar
                    : VersionPrerequisiteKind.InheritedVersion;
                result.Add(new VersionPrerequisiteInfo
                {
                    Kind = kind,
                    Id = current,
                    Display = kind == VersionPrerequisiteKind.ClientJar
                        ? $"客户端 jar（{current}）"
                        : $"前置原版 {current}"
                });
            }

            VersionJson doc;
            try
            {
                doc = JsonSerializer.Deserialize<VersionJson>(File.ReadAllText(jsonPath)) ?? new VersionJson();
            }
            catch
            {
                return; // json 损坏：不再继续推断，避免误报
            }

            if (doc.InheritsFrom is null) return;
            current = doc.InheritsFrom;
            if (!visited.Add(current)) return; // 防环
        }
    }

    /// <summary>检查该版本工作目录内 Mod 的强制前置依赖是否已安装。</summary>
    private static void ScanModDependencies(string? effectiveGameDir, List<VersionPrerequisiteInfo> result)
    {
        if (string.IsNullOrWhiteSpace(effectiveGameDir)) return;
        try
        {
            // 没有 mods 目录说明该版本不装 Mod，谈不上缺失前置
            if (!Directory.Exists(Path.Combine(effectiveGameDir, "mods"))) return;
        }
        catch
        {
            return;
        }

        try
        {
            foreach (var dep in ModManager.MissingDependencies(effectiveGameDir))
                result.Add(new VersionPrerequisiteInfo
                {
                    Kind = VersionPrerequisiteKind.ModDependency,
                    Id = dep,
                    Display = $"Mod 前置 {dep}"
                });
        }
        catch
        {
            // Mod 元数据解析失败时静默跳过，不阻塞列表展示
        }
    }
}
