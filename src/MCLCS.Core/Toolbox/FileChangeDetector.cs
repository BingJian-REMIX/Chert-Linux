using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MCLCS.Core.Toolbox;

/// <summary>快照中的单个文件。</summary>
public class FileSnapshotEntry
{
    [JsonPropertyName("path")] public string RelativePath { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("mtime")] public DateTime LastWriteUtc { get; set; }

    /// <summary>SHA-256（快速模式下为空）。</summary>
    [JsonPropertyName("hash")] public string? Hash { get; set; }
}

/// <summary>目录快照。</summary>
public class DirectorySnapshot
{
    [JsonPropertyName("root")] public string Root { get; set; } = "";
    [JsonPropertyName("takenAt")] public DateTime TakenAt { get; set; } = DateTime.Now;

    /// <summary>是否计算了哈希（false 表示只比对大小与修改时间）。</summary>
    [JsonPropertyName("hashed")] public bool Hashed { get; set; }

    [JsonPropertyName("entries")] public List<FileSnapshotEntry> Entries { get; set; } = new();

    public int Count => Entries.Count;
    public long TotalBytes => Entries.Sum(e => e.Size);
}

/// <summary>变更类型。</summary>
public enum FileChangeKind
{
    Added,
    Removed,
    Modified
}

/// <summary>一条变更。</summary>
public sealed class FileChange
{
    public FileChange(FileChangeKind kind, string path, string? detail = null)
    {
        Kind = kind;
        Path = path;
        Detail = detail;
    }

    public FileChangeKind Kind { get; }
    public string Path { get; }
    public string? Detail { get; }

    public string KindText => Kind switch
    {
        FileChangeKind.Added => "新增",
        FileChangeKind.Removed => "删除",
        FileChangeKind.Modified => "修改",
        _ => "?"
    };

    public override string ToString() => $"[{KindText}] {Path}" + (Detail is null ? "" : $" — {Detail}");
}

/// <summary>比对结果。</summary>
public sealed class SnapshotDiff
{
    public List<FileChange> Changes { get; } = new();

    public int Added => Changes.Count(c => c.Kind == FileChangeKind.Added);
    public int Removed => Changes.Count(c => c.Kind == FileChangeKind.Removed);
    public int Modified => Changes.Count(c => c.Kind == FileChangeKind.Modified);
    public bool HasChanges => Changes.Count > 0;

    public string Summary => HasChanges
        ? $"新增 {Added} 个，删除 {Removed} 个，修改 {Modified} 个"
        : "无变更";
}

/// <summary>
/// 文件变更检测（工具箱面板 5）：对目录拍快照并与后续状态比对。
/// 典型用途：装完整合包 / 更新 Mod 后，确认哪些文件被改动。
/// </summary>
/// <remarks>
/// 本类的每个方法都只接受「要查哪个目录」，本身不含版本概念 ——
/// 调用方需先用 <see cref="WatchDirFor"/> 按版本隔离规则解析出正确目录再传入。
/// </remarks>
public static class FileChangeDetector
{
    /// <summary>默认忽略的目录名。</summary>
    public static readonly string[] DefaultIgnoredDirs =
    {
        "logs", "crash-reports", "backups", ".git", "screenshots", "__pycache__"
    };

    /// <summary>
    /// 规格 2.3-16 要监视的目录：用户手动丢文件进来的三个位置。
    /// </summary>
    public static readonly string[] WatchTargets = { "mods", "resourcepacks", "shaderpacks" };

    /// <summary>
    /// 监视快照的持久化文件名。
    /// ★ 存在<b>被监视目录内</b>（共享 gameRoot 或隔离版 versions/&lt;id&gt;/），
    ///   如此隔离版与共享版各持一份基线 —— 两者本就不该混在一起比。
    /// </summary>
    public const string SnapshotFileName = "mclcs_watch_snapshot.json";

    public static string SnapshotPath(string gameRoot) => Path.Combine(gameRoot, SnapshotFileName);

    /// <summary>
    /// 解析某版本<b>实际应当被监视的目录</b>（版本隔离适配）。
    /// <para>隔离 → <c>versions/&lt;id&gt;/</c>；未隔离或 id 为空 → <c>gameRoot</c>。
    /// 委托 <see cref="Profiles.VersionIsolation.GameDirFor"/>，与启动时传给游戏的
    /// <c>--gameDir</c> 保持一致 —— 否则隔离版的 mods / config 变动<b>根本不会被检测到</b>
    /// （此前调用方一律传 gameRoot，等于只查了共享位置）。</para>
    /// </summary>
    public static string WatchDirFor(string gameRoot, string? versionId)
        => Profiles.VersionIsolation.GameDirFor(gameRoot, versionId ?? "");

    /// <summary>
    /// 枚举所有应当被监视的目录：共享 gameRoot + 每个<b>已开启隔离</b>的版本目录。
    /// <para>用于「启动器启动 / 焦点回到启动器」这类没有版本上下文的全局扫描 ——
    /// 只扫 gameRoot 会漏掉隔离版里的 mods / resourcepacks / shaderpacks 变动。</para>
    /// </summary>
    public static List<string> AllWatchDirs(string gameRoot)
    {
        var dirs = new List<string> { gameRoot };
        try
        {
            var versions = Utils.PathEx.VersionsDir(gameRoot);
            if (!Directory.Exists(versions)) return dirs;
            foreach (var d in Directory.EnumerateDirectories(versions))
            {
                var id = Path.GetFileName(d);
                if (Profiles.VersionIsolation.IsIsolated(gameRoot, id))
                    dirs.Add(d);
            }
        }
        catch { /* 目录枚举失败就退回只扫共享目录 */ }
        return dirs;
    }

    /// <summary>
    /// 对 <see cref="WatchTargets"/> 三个目录一次性拍快照，
    /// 相对路径带上子目录前缀（如 <c>mods/xxx.jar</c>），便于界面直接展示归属。
    /// 目录不存在时静默跳过（用户可能还没装过 Mod）。
    /// </summary>
    public static DirectorySnapshot TakeWatched(
        string gameRoot, IEnumerable<string>? targets = null, bool computeHash = false,
        CancellationToken ct = default, HashSet<string>? onlyRelativePaths = null)
    {
        var snapshot = new DirectorySnapshot { Root = gameRoot, Hashed = computeHash };

        foreach (var target in targets ?? WatchTargets)
        {
            ct.ThrowIfCancellationRequested();
            var dir = Path.Combine(gameRoot, target);
            if (!Directory.Exists(dir)) continue;

            // 第二段检测只取子集：把 "target/xxx" 还原为该目录下不带前缀的相对名
            HashSet<string>? onlyNames = null;
            if (onlyRelativePaths is not null)
            {
                onlyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var prefix = target + "/";
                foreach (var p in onlyRelativePaths)
                    if (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        onlyNames.Add(p.Substring(prefix.Length));
            }

            var sub = Take(dir, computeHash, ignoredDirs: null, ct, onlyNames);
            foreach (var e in sub.Entries)
            {
                e.RelativePath = $"{target}/{e.RelativePath}";
                snapshot.Entries.Add(e);
            }
        }

        snapshot.Entries.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return snapshot;
    }

    /// <summary>
    /// 检测自上次快照以来的变更：拍新快照，与磁盘上的旧快照比对，然后把新快照落盘。
    /// 首次运行（无旧快照）只建立基线，返回空 diff——否则会把用户现有的几百个 Mod
    /// 全部报成「新增」，那种通知没人想看。
    /// </summary>
    public static SnapshotDiff DetectAndUpdate(string gameRoot, CancellationToken ct = default)
    {
        var path = SnapshotPath(gameRoot);
        var before = Load(path);
        var after = TakeWatched(gameRoot, ct: ct);

        Save(after, path);

        return before is null ? new SnapshotDiff() : Compare(before, after);
    }

    /// <summary>
    /// 两段式自动检测（规格 2.3-16 / 用户需求：先比占用空间、变了再哈希）：
    /// ① 快：仅比对大小/修改时间（占用空间 + 单文件元数据），无变化直接返回，跳过昂贵的哈希；
    /// ② 慢但准：仅对①中疑似变更（新增/修改）的文件算 SHA-256，按内容确认是否真改
    ///    （剔除 mtime 抖动造成的误报），更新基线（全量元数据 + 已确认文件哈希）后返回 diff。
    /// 首次运行只静默建基线，不打扰。
    /// </summary>
    public static SnapshotDiff DetectTwoStage(string gameRoot, CancellationToken ct = default)
    {
        var path = SnapshotPath(gameRoot);
        var before = Load(path);
        if (before is null)
        {
            ResetBaseline(gameRoot, ct);   // 首次：建立基线
            return new SnapshotDiff();
        }

        // ① 快：元数据比对（全量，但不算哈希）
        var afterMeta = TakeWatched(gameRoot, computeHash: false, ct: ct);
        var quick = Compare(before, afterMeta);
        if (!quick.HasChanges)
        {
            Save(afterMeta, path);         // 无变化：仅更新元数据基线，不碰哈希
            return quick;
        }

        // ② 慢但准：只对疑似变更文件算哈希，确认内容是否真改
        var suspects = quick.Changes
            .Where(c => c.Kind != FileChangeKind.Removed)
            .Select(c => c.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var afterHashed = TakeWatched(gameRoot, computeHash: true, ct: ct, onlyRelativePaths: suspects);
        var diff = Compare(before, afterHashed);

        // 基线落盘：全量元数据 + 已确认变更文件的哈希（供下次精确比对）
        var hashMap = afterHashed.Entries.ToDictionary(e => e.RelativePath, e => e.Hash, StringComparer.OrdinalIgnoreCase);
        foreach (var e in afterMeta.Entries)
            if (hashMap.TryGetValue(e.RelativePath, out var h) && h is not null)
                e.Hash = h;
        Save(afterMeta, path);

        return diff;
    }

    /// <summary>只看「新增」的变更（手动丢文件的典型形态），供 Toast 通知使用。</summary>
    public static List<FileChange> NewFilesOnly(SnapshotDiff diff) =>
        diff.Changes.Where(c => c.Kind == FileChangeKind.Added).ToList();

    /// <summary>
    /// 只看不更新：对比当前状态与磁盘上的旧基线返回变更，但<b>不</b>落盘新快照。
    /// 供工具箱面板反复展示「自上次标记已知以来又变了什么」，而不影响基线本身。
    /// </summary>
    public static SnapshotDiff PreviewChanges(string gameRoot, CancellationToken ct = default)
    {
        var before = Load(SnapshotPath(gameRoot));
        var after = TakeWatched(gameRoot, ct: ct);
        return before is null ? new SnapshotDiff() : Compare(before, after);
    }

    /// <summary>建立/重置基线快照（用户点「标记为已知」时调用）。</summary>
    public static bool ResetBaseline(string gameRoot, CancellationToken ct = default) =>
        Save(TakeWatched(gameRoot, ct: ct), SnapshotPath(gameRoot));

    /// <summary>
    /// 对目录拍快照。<paramref name="computeHash"/> 为 true 时计算 SHA-256（慢但准确）。
    /// </summary>
    public static DirectorySnapshot Take(
        string root, bool computeHash = false,
        IEnumerable<string>? ignoredDirs = null,
        CancellationToken ct = default,
        HashSet<string>? onlyNames = null)
    {
        var snapshot = new DirectorySnapshot { Root = root, Hashed = computeHash };
        if (!Directory.Exists(root)) return snapshot;

        var ignores = new HashSet<string>(ignoredDirs ?? DefaultIgnoredDirs, StringComparer.OrdinalIgnoreCase);
        var rootFull = Path.GetFullPath(root);

        foreach (var file in EnumerateFiles(rootFull, ignores, ct))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(file);
                var rel = Normalize(Path.GetRelativePath(rootFull, file));
                if (onlyNames is not null && !onlyNames.Contains(rel)) continue;
                snapshot.Entries.Add(new FileSnapshotEntry
                {
                    RelativePath = rel,
                    Size = info.Length,
                    LastWriteUtc = info.LastWriteTimeUtc,
                    Hash = computeHash ? HashFile(file) : null
                });
            }
            catch
            {
                // 文件被占用 / 无权限：跳过
            }
        }

        snapshot.Entries.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return snapshot;
    }

    /// <summary>比对两份快照（纯函数）。</summary>
    public static SnapshotDiff Compare(DirectorySnapshot before, DirectorySnapshot after)
    {
        var diff = new SnapshotDiff();
        var beforeMap = before.Entries.ToDictionary(e => e.RelativePath, StringComparer.OrdinalIgnoreCase);
        var afterMap = after.Entries.ToDictionary(e => e.RelativePath, StringComparer.OrdinalIgnoreCase);

        foreach (var kv in afterMap)
        {
            if (!beforeMap.TryGetValue(kv.Key, out var old))
            {
                diff.Changes.Add(new FileChange(FileChangeKind.Added, kv.Key, FormatSize(kv.Value.Size)));
                continue;
            }

            var changed = old.Hash is not null && kv.Value.Hash is not null
                ? !string.Equals(old.Hash, kv.Value.Hash, StringComparison.OrdinalIgnoreCase)
                : old.Size != kv.Value.Size || old.LastWriteUtc != kv.Value.LastWriteUtc;

            if (changed)
            {
                var detail = old.Size != kv.Value.Size
                    ? $"{FormatSize(old.Size)} → {FormatSize(kv.Value.Size)}"
                    : "内容已变化";
                diff.Changes.Add(new FileChange(FileChangeKind.Modified, kv.Key, detail));
            }
        }

        foreach (var kv in beforeMap.Where(kv => !afterMap.ContainsKey(kv.Key)))
            diff.Changes.Add(new FileChange(FileChangeKind.Removed, kv.Key, FormatSize(kv.Value.Size)));

        diff.Changes.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return diff;
    }

    /// <summary>保存快照到 JSON。</summary>
    public static bool Save(DirectorySnapshot snapshot, string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(snapshot));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>从 JSON 载入快照；失败返回 null。</summary>
    public static DirectorySnapshot? Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<DirectorySnapshot>(File.ReadAllText(path))
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> EnumerateFiles(string root, HashSet<string> ignores, CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();

            string[] subDirs;
            string[] files;
            try
            {
                subDirs = Directory.GetDirectories(dir);
                files = Directory.GetFiles(dir);
            }
            catch
            {
                continue;
            }

            foreach (var sub in subDirs)
                if (!ignores.Contains(Path.GetFileName(sub)))
                    stack.Push(sub);

            foreach (var f in files) yield return f;
        }
    }

    private static string Normalize(string p) => p.Replace('\\', '/');

    private static string HashFile(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / 1024.0 / 1024:F1} MB"
    };
}
