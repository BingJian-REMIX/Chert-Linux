using System.Text.RegularExpressions;

namespace MCLCS.Core.Save;

/// <summary>存档损坏严重程度。</summary>
public enum SaveCorruptionSeverity
{
    /// <summary>未发现明显损坏。</summary>
    Ok,

    /// <summary>可疑 / 次级问题（如存在 level.dat_old 备份、区域文件大小异常），不一定无法加载。</summary>
    Warning,

    /// <summary>已损坏，很可能导致无法加载或地形异常（如缺少/损坏 level.dat、0 字节区域文件）。</summary>
    Corrupt
}

/// <summary>单个存档的损坏检测结果（只读，不修复）。</summary>
public class SaveCorruptionReport
{
    public string SaveName { get; set; } = "";
    public string SavePath { get; set; } = "";

    /// <summary>是否存在致命损坏（无法加载）。</summary>
    public bool IsCorrupt { get; set; }

    public SaveCorruptionSeverity Severity { get; set; } = SaveCorruptionSeverity.Ok;

    /// <summary>明确的问题清单（致命或损坏级）。</summary>
    public List<string> Issues { get; } = new();

    /// <summary>提示性说明（非致命，如存在自动备份可恢复）。</summary>
    public List<string> Notes { get; } = new();

    public string Summary
    {
        get
        {
            var detail = Issues.Count > 0
                ? string.Join("；", Issues)
                : string.Join("；", Notes);
            return Severity switch
            {
                SaveCorruptionSeverity.Corrupt => $"⚠ 已损坏，可能无法加载：{detail}",
                SaveCorruptionSeverity.Warning => $"⚠ 可疑，建议先备份再进入：{detail}",
                _ => "✓ 未检测到损坏。"
            };
        }
    }
}

/// <summary>
/// 游戏存档损坏检测（§三 存档修复的"只检测不修复"部分）。
/// <para>
/// 扫描每个世界的 <c>level.dat</c>（缺失 / NBT 不可解析）与区域文件 <c>*.mca</c>
/// （0 字节 / 大小非 4096 对齐，通常是被截断或写入中断导致）。全程只读，不修改任何文件。
/// 检测到问题后交由 UI 展示，由用户自行决定用备份恢复或第三方工具修复。
/// </para>
/// </summary>
public static class SaveCorruptionDetector
{
    public static string SavesDir(string gameRoot) => Path.Combine(gameRoot, "saves");

    /// <summary>扫描全部存档的损坏情况（跳过备份目录）。</summary>
    public static List<SaveCorruptionReport> Scan(string gameRoot)
    {
        var result = new List<SaveCorruptionReport>();

        // 待扫描根：共享 saves/ + 各版本隔离 versions/<id>/saves/
        // 仅扫共享目录会漏掉版本隔离存档，且共享/隔离同名世界会被 ScanCorruptionAsync 按 SaveName 误匹配。
        var scanRoots = new List<string> { SavesDir(gameRoot) };
        var versionsDir = Path.Combine(gameRoot, "versions");
        if (Directory.Exists(versionsDir))
        {
            foreach (var v in Directory.GetDirectories(versionsDir))
            {
                var iso = Path.Combine(v, "saves");
                if (Directory.Exists(iso)) scanRoots.Add(iso);
            }
        }

        foreach (var savesDir in scanRoots)
        {
            if (!Directory.Exists(savesDir)) continue;
            foreach (var dir in Directory.GetDirectories(savesDir))
            {
                var name = Path.GetFileName(dir);
                if (name.EndsWith(".backup", StringComparison.OrdinalIgnoreCase)
                    || Regex.IsMatch(name, @"\.backup-\d{14}$")) continue;

                var report = ScanSingle(dir);
                if (report is not null) result.Add(report);
            }
        }
        return result;
    }

    /// <summary>检测单个存档；无法判定时返回 null。</summary>
    public static SaveCorruptionReport? ScanSingle(string savePath)
    {
        var name = Path.GetFileName(savePath);
        var report = new SaveCorruptionReport { SaveName = name, SavePath = savePath };

        // 1) level.dat 缺失
        var levelDat = Path.Combine(savePath, "level.dat");
        if (!File.Exists(levelDat))
        {
            report.IsCorrupt = true;
            report.Severity = SaveCorruptionSeverity.Corrupt;
            report.Issues.Add("缺少 level.dat，世界无法加载。");
            return report;
        }

        // 2) level.dat 是否可解析（NBT gzip）
        try
        {
            var root = NbtFile.ReadGzip(levelDat);
            if (root is null || root.GetDataVersion() == 0 && root.Find("Data") is null)
            {
                report.IsCorrupt = true;
                report.Severity = SaveCorruptionSeverity.Corrupt;
                report.Issues.Add("level.dat 结构异常，可能无法被游戏识别。");
            }
        }
        catch (Exception ex)
        {
            report.IsCorrupt = true;
            report.Severity = SaveCorruptionSeverity.Corrupt;
            report.Issues.Add($"level.dat 已损坏，无法解析：{ex.Message}");
        }

        // 3) 区域文件 *.mca 损坏检测（递归，覆盖主世界/下界/末地及自定义维度）
        try
        {
            foreach (var mca in Directory.EnumerateFiles(savePath, "*.mca", SearchOption.AllDirectories))
            {
                var fi = new FileInfo(mca);
                var rel = Path.GetRelativePath(savePath, mca);
                if (fi.Length == 0)
                {
                    report.Severity = SaveCorruptionSeverity.Corrupt;
                    report.IsCorrupt = true;
                    report.Issues.Add($"区域文件 {rel} 大小为 0，已损坏（加载时可能崩溃或地形缺失）。");
                }
                else if (fi.Length % 4096 != 0)
                {
                    if (report.Severity < SaveCorruptionSeverity.Warning)
                        report.Severity = SaveCorruptionSeverity.Warning;
                    report.Notes.Add($"区域文件 {rel} 大小异常（{fi.Length} 字节，非 4096 对齐），可能不完整。");
                }
            }
        }
        catch (Exception ex)
        {
            report.Notes.Add($"区域文件扫描失败：{ex.Message}");
        }

        // 4) level.dat_old：上次崩溃的自动备份（提示可用备份恢复）
        if (File.Exists(Path.Combine(savePath, "level.dat_old")))
        {
            if (report.Severity < SaveCorruptionSeverity.Warning)
                report.Severity = SaveCorruptionSeverity.Warning;
            report.Notes.Add("检测到 level.dat_old（上次崩溃的自动备份），可尝试用其恢复。");
        }

        return report;
    }
}
