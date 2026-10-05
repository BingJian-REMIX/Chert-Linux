using System.Collections.Generic;
using System.Diagnostics;
using MCLCS.Core.Music;
using MCLCS.Core.Profiles;
using MCLCS.Linux.App;
using Xunit;

namespace MCLCS.Linux.Tests;

/// <summary>
/// 本地客户端生命周期（宽限期后自动结束客户端）的判定逻辑。
///
/// <para><b>测试策略</b>：管理器接受注入的探测器 / 设置 / 模式提供者，
/// 因此完全不需要真实客户端进程 —— 用当前进程 PID 占位即可。
/// 判定结果只取决于注入的假探测器，稳定且不产生副作用。
/// <b>刻意不测「到期 Kill」</b>：那会真的结束进程。</para>
/// </summary>
public class ClientLifecycleTests
{
    private sealed class FakeProbe : IClientPlaybackProbe
    {
        public Dictionary<int, ClientPlaybackState>? States { get; set; }
        public Dictionary<int, ClientPlaybackState>? GetStates(IReadOnlyList<int> pids) => States;
    }

    private static (ClientLifecycleManager mgr, FakeProbe probe, MusicClientPrefs prefs) Build(
        MusicSourceMode mode = MusicSourceMode.Api)
    {
        var probe = new FakeProbe();
        var prefs = new MusicClientPrefs { Enabled = true, GraceMinutes = 30 };
        var mgr = new ClientLifecycleManager(probe, () => prefs, () => mode);
        return (mgr, probe, prefs);
    }

    private static int SelfPid => Process.GetCurrentProcess().Id;

    [Fact]
    public void 宽限期为0时_不启动计时()
    {
        var (mgr, probe, prefs) = Build();
        prefs.GraceMinutes = 0;
        probe.States = new Dictionary<int, ClientPlaybackState> { [SelfPid] = ClientPlaybackState.Paused };

        mgr.Register(SelfPid, "self");
        mgr.Tick();

        Assert.Equal(-1, mgr.RemainingGraceSeconds);   // 整体跳过自动关闭
    }

    [Fact]
    public void 总开关关闭时_不启动计时()
    {
        var (mgr, probe, prefs) = Build();
        prefs.Enabled = false;
        probe.States = new Dictionary<int, ClientPlaybackState> { [SelfPid] = ClientPlaybackState.Paused };

        mgr.Register(SelfPid, "self");
        mgr.Tick();

        Assert.Equal(-1, mgr.RemainingGraceSeconds);
    }

    [Fact]
    public void 探测返回Unknown时_本轮跳过_不误杀()
    {
        var (mgr, probe, _) = Build();
        probe.States = new Dictionary<int, ClientPlaybackState> { [SelfPid] = ClientPlaybackState.Unknown };

        mgr.Register(SelfPid, "self");
        mgr.Tick();

        // ★ 探测失败绝不能被当成「已暂停」，否则会误杀正在播放的客户端
        Assert.Equal(-1, mgr.RemainingGraceSeconds);
        Assert.Equal(1, mgr.TrackedCount);
    }

    [Fact]
    public void 探测失败返回null时_本轮跳过()
    {
        var (mgr, probe, _) = Build();
        probe.States = null;

        mgr.Register(SelfPid, "self");
        mgr.Tick();

        Assert.Equal(-1, mgr.RemainingGraceSeconds);
    }

    [Fact]
    public void 全部暂停时_启动计时()
    {
        var (mgr, probe, _) = Build();
        probe.States = new Dictionary<int, ClientPlaybackState> { [SelfPid] = ClientPlaybackState.Paused };

        mgr.Register(SelfPid, "self");
        mgr.Tick();

        Assert.True(mgr.RemainingGraceSeconds >= 0);
        Assert.True(mgr.RemainingGraceSeconds <= 30 * 60);
    }

    [Fact]
    public void 任一侧在播放_重置计时()
    {
        var (mgr, probe, _) = Build();
        var paused = new Dictionary<int, ClientPlaybackState> { [SelfPid] = ClientPlaybackState.Paused };

        mgr.Register(SelfPid, "self");
        probe.States = paused;
        mgr.Tick();
        Assert.True(mgr.RemainingGraceSeconds >= 0);

        // 恢复播放 → 清零
        probe.States = new Dictionary<int, ClientPlaybackState> { [SelfPid] = ClientPlaybackState.Playing };
        mgr.Tick();
        Assert.Equal(-1, mgr.RemainingGraceSeconds);
    }

    [Fact]
    public void 切回本地客户端模式_重置计时()
    {
        var (mgr, probe, _) = Build(MusicSourceMode.LocalClient);
        probe.States = new Dictionary<int, ClientPlaybackState> { [SelfPid] = ClientPlaybackState.Paused };

        mgr.Register(SelfPid, "self");
        mgr.Tick();

        Assert.Equal(-1, mgr.RemainingGraceSeconds);   // 客户端本就该继续播
    }

    [Fact]
    public void 重复登记同一PID_只记一次()
    {
        var (mgr, _, _) = Build();
        mgr.Register(SelfPid, "self");
        mgr.Register(SelfPid, "self");

        Assert.Equal(1, mgr.TrackedCount);   // 否则到期会 Kill 两次
    }

    [Fact]
    public void 无效PID_被忽略()
    {
        var (mgr, _, _) = Build();
        mgr.Register(0, "x");
        mgr.Register(-1, "x");

        Assert.Equal(0, mgr.TrackedCount);
    }

    [Fact]
    public void 已退出的进程_在一轮判定中被清理()
    {
        var (mgr, probe, _) = Build();
        probe.States = new Dictionary<int, ClientPlaybackState> { [SelfPid] = ClientPlaybackState.Paused };

        // 用一个几乎必然不存在的高位 PID 模拟「已自行退出」
        mgr.Register(999999, "ghost");
        mgr.Tick();

        Assert.Equal(0, mgr.TrackedCount);
        Assert.Equal(-1, mgr.RemainingGraceSeconds);
    }

    [Fact]
    public void ClearAll_清空并停止计时()
    {
        var (mgr, probe, _) = Build();
        probe.States = new Dictionary<int, ClientPlaybackState> { [SelfPid] = ClientPlaybackState.Paused };

        mgr.Register(SelfPid, "self");
        mgr.Tick();
        Assert.True(mgr.RemainingGraceSeconds >= 0);

        mgr.ClearAll();
        Assert.Equal(0, mgr.TrackedCount);
        Assert.Equal(-1, mgr.RemainingGraceSeconds);
    }

    [Fact]
    public void Unregister_移除指定进程()
    {
        var (mgr, _, _) = Build();
        mgr.Register(SelfPid, "self");
        mgr.Unregister(SelfPid);

        Assert.Equal(0, mgr.TrackedCount);
    }

    [Fact]
    public void 空PID列表_探测器返回null()
    {
        var probe = new CpuActivityPlaybackProbe();
        Assert.Null(probe.GetStates(new List<int>()));
        Assert.Null(probe.GetStates(null!));
    }

    [Fact]
    public void 首次采样_建立基线并返回Unknown()
    {
        var probe = new CpuActivityPlaybackProbe();
        var pids = new List<int> { SelfPid };

        var first = probe.GetStates(pids);
        Assert.NotNull(first);
        Assert.Equal(ClientPlaybackState.Unknown, first![SelfPid]);   // 宁可多等一轮也不误杀
    }

    [Fact]
    public void 自动关闭开关_由宽限期派生()
    {
        Assert.False(new MusicClientPrefs { GraceMinutes = 0 }.AutoCloseEnabled);
        Assert.True(new MusicClientPrefs { GraceMinutes = 1 }.AutoCloseEnabled);
    }

    [Fact]
    public void 宽限期_越界值被钳制()
    {
        Assert.Equal(MusicClientPrefs.MaxGraceMinutes,
            new MusicClientPrefs { GraceMinutes = 9999 }.Normalized().GraceMinutes);
        Assert.Equal(MusicClientPrefs.MinGraceMinutes,
            new MusicClientPrefs { GraceMinutes = -5 }.Normalized().GraceMinutes);
    }

    [Fact]
    public void 模式管理器_总开关关闭时_本地客户端模式降级为Api()
    {
        MusicModeManager.ResetForTests();
        var prefs = new MusicClientPrefs { Enabled = false };

        Assert.Equal(MusicSourceMode.Api, MusicModeManager.SwitchTo(MusicSourceMode.LocalClient, prefs));
        MusicModeManager.ResetForTests();
    }

    [Fact]
    public void 模式管理器_切换后广播当前模式()
    {
        MusicModeManager.ResetForTests();
        var prefs = new MusicClientPrefs { Enabled = true };
        MusicSourceMode? heard = null;
        void OnChanged(MusicSourceMode m) => heard = m;

        MusicModeManager.ModeChanged += OnChanged;
        try
        {
            MusicModeManager.SwitchTo(MusicSourceMode.LocalClient, prefs);
            Assert.Equal(MusicSourceMode.LocalClient, heard);
            Assert.Equal(MusicSourceMode.LocalClient, MusicModeManager.Current);
        }
        finally
        {
            MusicModeManager.ModeChanged -= OnChanged;
            MusicModeManager.ResetForTests();
        }
    }
}
