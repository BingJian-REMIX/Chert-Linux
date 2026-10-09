using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MCLCS.Linux.App.Themes;

/// <summary>
/// 清单 #42：限时活动提醒 / 倒计时卡片。
/// 日期沿用 <see cref="SeasonEntry"/> 的 MM-dd 约定（不带年份、逐年复用），
/// 也接受 yyyy-MM-dd（用于一次性的周年庆 / Minecon 等）。
/// </summary>
public sealed class SeasonalEventEntry
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    /// <summary>活动开始（MM-dd 或 yyyy-MM-dd）。</summary>
    [JsonPropertyName("start")]
    public string Start { get; set; } = "";

    /// <summary>活动结束（含当日）。</summary>
    [JsonPropertyName("end")]
    public string End { get; set; } = "";

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    /// <summary>详情链接（可为空）。</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>限定到某个节日；为空表示全时段展示。</summary>
    [JsonPropertyName("season")]
    public string? Season { get; set; }
}

/// <summary>清单 #43：节日服务器推荐条目。</summary>
public sealed class SeasonalServerEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>服务器地址（host 或 host:port）。</summary>
    [JsonPropertyName("address")]
    public string Address { get; set; } = "";

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("season")]
    public string? Season { get; set; }
}

/// <summary>推荐内容类型（清单 #44：版本 / Mod / 材质包 / 整合包）。</summary>
public enum SeasonalPickKind
{
    Unknown,
    Mod,
    Modpack,
    ResourcePack,
    Shader,
    Version
}

/// <summary>清单 #44：按节日主题整理的内容推荐。</summary>
public sealed class SeasonalPickEntry
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "mod";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    /// <summary>Modrinth slug / 版本号等标识，仅作展示与拼接链接用。</summary>
    [JsonPropertyName("slug")]
    public string? Slug { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("season")]
    public string? Season { get; set; }

    [JsonIgnore]
    public SeasonalPickKind KindEnum => Kind?.Trim().ToLowerInvariant() switch
    {
        "mod" => SeasonalPickKind.Mod,
        "modpack" => SeasonalPickKind.Modpack,
        "resourcepack" or "resource_pack" or "texture" => SeasonalPickKind.ResourcePack,
        "shader" or "shaderpack" => SeasonalPickKind.Shader,
        "version" => SeasonalPickKind.Version,
        _ => SeasonalPickKind.Unknown
    };

    /// <summary>无显式链接时按类型拼一个 Modrinth 页面。</summary>
    [JsonIgnore]
    public string? ResolvedUrl
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Url)) return Url;
            if (string.IsNullOrWhiteSpace(Slug)) return null;
            var path = KindEnum switch
            {
                SeasonalPickKind.Modpack => "modpack",
                SeasonalPickKind.ResourcePack => "resourcepack",
                SeasonalPickKind.Shader => "shader",
                SeasonalPickKind.Mod => "mod",
                _ => null
            };
            return path is null ? null : $"https://modrinth.com/{path}/{Slug}";
        }
    }
}

/// <summary>清单 #31：节日公告（纯静态 JSON，启动后弹一次）。</summary>
public sealed class SeasonalAnnouncement
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("season")]
    public string? Season { get; set; }

    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrWhiteSpace(Title) && string.IsNullOrWhiteSpace(Message);
}

/// <summary>清单 #28：节目音频（BGM / 按钮音效）。均为可选 URL，缺失即静默不播放。</summary>
public sealed class SeasonalAudioConfig
{
    /// <summary>节日 BGM 直链（建议 mp3/ogg/wav）。</summary>
    [JsonPropertyName("bgm")]
    public string? Bgm { get; set; }

    /// <summary>按钮点击音效直链。</summary>
    [JsonPropertyName("click")]
    public string? Click { get; set; }

    /// <summary>BGM 音量（0~1）。</summary>
    [JsonPropertyName("volume")]
    public double Volume { get; set; } = 0.35;
}

/// <summary>
/// 节日内容视图模型（界面绑定用）：把配置条目换算成可直接展示的文本。
/// </summary>
public sealed class SeasonalEventCard
{
    public string Title { get; init; } = "";
    public string Note { get; init; } = "";
    public string? Url { get; init; }

    /// <summary>距离开始的剩余天数；已开始则为 0。</summary>
    public int DaysUntilStart { get; init; }

    /// <summary>距离结束的剩余天数（含当日）；已结束为负数。</summary>
    public int DaysUntilEnd { get; init; }

    public bool Started { get; init; }
    public bool Ended { get; init; }

    /// <summary>倒计时文案，如「还有 12 天开始」/「进行中 · 剩 3 天」/「已结束」。</summary>
    public string CountdownText { get; init; } = "";
}
