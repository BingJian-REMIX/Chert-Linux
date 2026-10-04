using System.Text.Json.Serialization;

namespace MCLCS.Core.Profiles;

/// <summary>
/// 歌词固定方式：叠加层除当前行外，还固定显示上下哪几行。
/// 只影响叠加层的渲染行数，不影响歌词解析与查找。
/// </summary>
public enum LyricPinMode
{
    /// <summary>仅当前行。</summary>
    CurrentOnly = 0,

    /// <summary>当前行 + 下一行。</summary>
    CurrentAndNext = 1,

    /// <summary>上一行 + 当前行 + 下一行。</summary>
    PrevCurrentNext = 2
}

/// <summary>
/// 本地客户端模式的相关设置（音乐播放器 · 规格「设置项定义」）。
///
/// <para><b>容错</b>：反序列化的 JSON 可能被手改、可能来自旧版本或跨设备同步，
/// 因此取值一律经 <see cref="Normalized"/> 归一 —— 数值钳制到合法区间、
/// 枚举非法值回退默认。这样即便配置文件被写坏，也不会让设置页出现
/// 「宽限期 = 9999 分钟」或歌词行数取到不存在的枚举值。</para>
///
/// <para><b>联动</b>：总开关 <see cref="Enabled"/> 关闭时后三项在 UI 中灰显/折叠；
/// 且 <see cref="GraceMinutes"/> 为 0 时生命周期管理器<b>完全跳过</b>自动关闭逻辑。</para>
/// </summary>
public sealed class MusicClientPrefs
{
    /// <summary>总开关默认值（关闭）。</summary>
    public const bool DefaultEnabled = false;

    /// <summary>客户端宽限期默认值（分钟）。</summary>
    public const int DefaultGraceMinutes = 5;

    /// <summary>宽限期下限（分钟）。</summary>
    public const int MinGraceMinutes = 0;

    /// <summary>宽限期上限（分钟）。</summary>
    public const int MaxGraceMinutes = 30;

    /// <summary>启用本地客户端模式（总开关）。关闭时完全不涉及外部客户端进程。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = DefaultEnabled;

    /// <summary>
    /// 客户端宽限期（分钟，0–30）。
    /// 播放器切到本地文件夹或 API 模式、且所有由启动器拉起的客户端均处于暂停状态时，
    /// 持续此时间后直接结束这些客户端进程。<b>设为 0 表示不自动关闭</b>。
    /// </summary>
    [JsonPropertyName("graceMinutes")]
    public int GraceMinutes { get; set; } = DefaultGraceMinutes;

    /// <summary>歌词固定方式（只影响叠加层渲染行数）。</summary>
    [JsonPropertyName("lyricPin")]
    public LyricPinMode LyricPin { get; set; } = LyricPinMode.CurrentOnly;

    /// <summary>
    /// 客户端模式下仍用 API 获取歌词。开启时叠加层仍显示歌词，关闭则隐藏。
    /// </summary>
    [JsonPropertyName("lyricEnabled")]
    public bool LyricEnabled { get; set; } = true;

    /// <summary>
    /// 归一化到合法区间：宽限期钳制到 0–30，非法枚举回退默认。
    /// 反序列化后执行一次，避免脏配置渗进业务逻辑。
    /// </summary>
    public MusicClientPrefs Normalized()
    {
        if (GraceMinutes < MinGraceMinutes) GraceMinutes = MinGraceMinutes;
        else if (GraceMinutes > MaxGraceMinutes) GraceMinutes = MaxGraceMinutes;

        if (!System.Enum.IsDefined(typeof(LyricPinMode), LyricPin))
            LyricPin = LyricPinMode.CurrentOnly;

        return this;
    }

    /// <summary>宽限期是否开启了自动关闭（0 表示不自动关闭，生命周期管理器应整体跳过）。</summary>
    public bool AutoCloseEnabled => GraceMinutes > 0;
}
