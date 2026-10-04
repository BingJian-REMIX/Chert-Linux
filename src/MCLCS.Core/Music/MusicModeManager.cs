using MCLCS.Core.Profiles;

namespace MCLCS.Core.Music;

/// <summary>
/// 音乐音源模式（规格 · 实现项 5「模式管理器」）。
/// </summary>
/// <remarks>
/// 与 <c>MusicPlayerViewModel.PlayMode</c>（列表循环 / 顺序 / 单曲 / 随机）**是两个不同维度**：
/// 那是「怎么播」，这里是「从哪放」，不要混用。
/// </remarks>
public enum MusicSourceMode
{
    /// <summary>API 模式：由启动器自己通过网络接口取流并播放。</summary>
    Api = 0,

    /// <summary>本地文件夹模式：播放用户本地音乐文件。</summary>
    LocalFolder = 1,

    /// <summary>本地客户端模式：启动器不自己放，交给外部客户端（QQ 音乐 / 网易云等）。</summary>
    LocalClient = 2
}

/// <summary>
/// 音源模式管理器（规格 · 实现项 5）。
///
/// <para><b>职责</b>：维护三种模式的当前状态；模式切换时通知
/// ①客户端生命周期管理器 ②歌词引擎 ③叠加层 ④迷你播放条。</para>
///
/// <para><b>不变量：同一时间只有一个音源在播放。</b>切到某模式时先停掉上一个音源 ——
/// 否则会出现「启动器还在放自己的歌，同时外部客户端也在放」的双份声音。</para>
///
/// <para><b>为什么单独抽一个类</b>：模式切换牵涉 4 个子系统，若散落在各 VM 里
/// 极易漏掉某个通知（曾经就出现过「启动了客户端但计时器还在跑」这类问题）。
/// 集中到这里后，「切模式」只有一个入口，通知不会漏。</para>
/// </summary>
public static class MusicModeManager
{
    private static MusicSourceMode _current = MusicSourceMode.Api;

    /// <summary>由启动器提供的「停止当前音源」回调（由播放器界面层注入）。</summary>
    public static Action? StopLocalPlayback { get; set; }

    /// <summary>模式切换事件：参数为新模式。</summary>
    public static event Action<MusicSourceMode>? ModeChanged;

    /// <summary>当前音源模式。</summary>
    public static MusicSourceMode Current => _current;

    /// <summary>是否处于本地客户端模式（总开关关闭时恒为 false）。</summary>
    public static bool IsLocalClientMode(MusicClientPrefs prefs) =>
        prefs.Enabled && _current == MusicSourceMode.LocalClient;

    /// <summary>
    /// 切换音源模式。会先停掉上一个音源，再广播 <see cref="ModeChanged"/>。
    /// </summary>
    /// <param name="mode">目标模式。</param>
    /// <param name="prefs">
    /// 本地客户端模式设置。<b>总开关关闭时，切到 <see cref="MusicSourceMode.LocalClient"/>
    /// 会被降级为 <see cref="MusicSourceMode.Api"/></b> —— 总开关的语义是
    /// 「关闭时完全不涉及外部客户端进程」，因此不能进本地客户端模式。
    /// </param>
    /// <returns>实际生效的模式（可能被降级）。</returns>
    public static MusicSourceMode SwitchTo(MusicSourceMode mode, MusicClientPrefs prefs)
    {
        var target = mode;

        // 总开关关闭时不得进入本地客户端模式
        if (target == MusicSourceMode.LocalClient && !prefs.Enabled)
            target = MusicSourceMode.Api;

        if (target == _current) return _current;

        // 离开本地客户端模式时，先停掉「启动器自己」正在放的声音 ——
        // 否则切走后会与外部客户端叠音。
        if (_current != MusicSourceMode.LocalClient)
        {
            try { StopLocalPlayback?.Invoke(); }
            catch { /* 停止失败不影响模式切换 */ }
        }

        _current = target;

        try { ModeChanged?.Invoke(target); }
        catch { /* 某个订阅者出错不应影响其它订阅者 */ }

        return target;
    }

    /// <summary>
    /// 重置为 API 模式（不广播、不停止音源）。
    /// 供单元测试与「音源初始化」使用 —— 静态状态的默认位置不一定是 API。
    /// </summary>
    public static void ResetForTests() => _current = MusicSourceMode.Api;
}
