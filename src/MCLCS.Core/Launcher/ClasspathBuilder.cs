using MCLCS.Core.Models;
using MCLCS.Core.Utils;

namespace MCLCS.Core.Launcher;

/// <summary>一个需要解压的原生库。</summary>
public class NativeEntry
{
    /// <summary>本地原生 jar 路径（libraries 下）。</summary>
    public string JarPath { get; set; } = "";

    /// <summary>解压目标目录（natives 目录）。</summary>
    public string ExtractDir { get; set; } = "";

    /// <summary>解压时需排除的前缀（如 META-INF/）。</summary>
    public List<string> Exclude { get; set; } = new();
}

/// <summary>
/// classPath 构建与原生库处理。
/// - 由 libraries + 版本主 jar（继承链上存在的）生成完整 classpath。
/// - 列出当前平台的 natives 分类 jar，供下载与解压。
/// </summary>
public static class ClasspathBuilder
{
    /// <summary>生成 classpath（平台分隔符连接）。要求相关 jar 已存在于本地。</summary>
    public static string ComputeClasspath(string gameRoot, string leafId, VersionJson merged)
    {
        var osName = RuleEvaluator.CurrentOsName();
        var entries = new List<string>();

        // 继承链上存在的版本主 jar
        foreach (var vid in VersionMerger.GetHierarchy(gameRoot, leafId))
        {
            var jar = PathEx.VersionJarPath(gameRoot, vid);
            if (File.Exists(jar)) entries.Add(jar);
        }

        // 普通库 artifact
        foreach (var lib in merged.Libraries)
        {
            if (!RuleEvaluator.IsAllowed(lib.Rules, osName)) continue;
            if (lib.Downloads?.Artifact?.Path is { } path)
                entries.Add(Path.Combine(gameRoot, "libraries", path));
            else if (lib.Downloads?.Artifact is not null && lib.Downloads.Artifact.Path is null)
                continue; // 有 artifact 但无 path，跳过
            // 无 downloads 时按 Maven 坐标推断本地路径
            else if (lib.Downloads is null && !string.IsNullOrEmpty(lib.Name) && lib.Natives is null)
                entries.Add(Path.Combine(gameRoot, "libraries", lib.Coordinate.LocalPath()));
        }

        return string.Join(Path.PathSeparator.ToString(), entries);
    }

    /// <summary>列出当前平台需要解压的原生库条目。</summary>
    public static List<NativeEntry> GetNativeEntries(string gameRoot, VersionJson merged, string nativesDir)
    {
        var osName = RuleEvaluator.CurrentOsName();
        var list = new List<NativeEntry>();

        foreach (var lib in merged.Libraries)
        {
            if (lib.Natives is null) continue;
            if (!RuleEvaluator.IsAllowed(lib.Rules, osName)) continue;
            if (!lib.Natives.TryGetValue(osName, out var classifier)) continue;

            // 优先取 downloads.classifiers 中的真实路径；缺失时用 Maven 坐标推断本地 jar 路径，
            // 兼容其他启动器写出的旧格式 JSON（只有 name、没有 downloads.classifiers）。
            string jarPath;
            if (lib.Downloads?.Classifiers is not null
                && lib.Downloads.Classifiers.TryGetValue(classifier, out var info)
                && info is not null && info.Path is not null)
            {
                jarPath = Path.Combine(gameRoot, "libraries", info.Path);
            }
            else
            {
                jarPath = Path.Combine(gameRoot, "libraries", lib.Coordinate.LocalPath(classifier));
            }

            list.Add(new NativeEntry
            {
                JarPath = jarPath,
                ExtractDir = nativesDir,
                Exclude = lib.Extract?.Exclude ?? new List<string>()
            });
        }

        return list;
    }

    /// <summary>将本地已存在的原生 jar 解压到 natives 目录。</summary>
    public static void ExtractNatives(List<NativeEntry> entries)
    {
        foreach (var e in entries)
        {
            if (!File.Exists(e.JarPath)) continue;
            Unzip.ExtractToDirectory(e.JarPath, e.ExtractDir, e.Exclude);
        }
    }
}
