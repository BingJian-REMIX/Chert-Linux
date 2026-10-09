namespace MCLCS.Core.Mods;

/// <summary>单个 Mod 文件的核验结果（清单 #66）。</summary>
public class ModVerifyResult
{
    /// <summary>被核验的文件名（含 .jar / .jar.disabled）。</summary>
    public string FileName { get; set; } = "";

    /// <summary>文件是否存在于 mods 目录。</summary>
    public bool Exists { get; set; }

    /// <summary>文件体积（字节）。</summary>
    public long SizeBytes { get; set; }

    /// <summary>能否作为 zip/jar 正常打开（完整性）。</summary>
    public bool IsValidJar { get; set; }

    /// <summary>是否解析出 fabric.mod.json / mods.toml 元数据。</summary>
    public bool HasMetadata { get; set; }

    /// <summary>SHA-1（Modrinth 用 sha1 / sha512 标识文件），便于人工比对。</summary>
    public string Sha1 { get; set; } = "";

    /// <summary>人类可读结论。</summary>
    public string Message { get; set; } = "";

    /// <summary>是否完全通过。</summary>
    public bool Ok => Exists && IsValidJar && HasMetadata;

    /// <summary>等级：none（未核验）/ ok / warn / error。</summary>
    public string Level
    {
        get
        {
            if (!Exists) return "error";
            if (!IsValidJar) return "error";
            return HasMetadata ? "ok" : "warn";
        }
    }
}

/// <summary>一键更新单个 Mod 的结果（清单 #66）。</summary>
public class ModUpdateOutcome
{
    /// <summary>是否成功替换到新版本。</summary>
    public bool Success { get; set; }

    /// <summary>结果说明（成功时为版本号，失败时为原因）。</summary>
    public string Message { get; set; } = "";

    /// <summary>更新后落盘的文件名，失败时为 null。</summary>
    public string? NewFileName { get; set; }
}
