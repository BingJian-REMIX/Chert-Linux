using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia.Threading;
using MCLCS.Core.Music;
using MCLCS.Core.Profiles;

namespace MCLCS.Linux.App;

/// <summary>
/// 本地客户端生命周期管理（规格 · 实现项 2）。
///
/// <para><b>职责</b>：记录所有<b>由启动器拉起</b>的外部音乐客户端进程；
/// 当播放器切到本地文件夹 / API 模式、且这些客户端全部处于暂停状态并持续
/// 「客户端宽限期」后，直接 Kill 掉它们。</para>
///
/// <para><b>为什么 Kill 而不是优雅关闭</b>（规格已确认）：某些客户端在后台播放时
/// 收到「关闭」指令会弹出前台关闭动画/确认框，既突兀又可能卡住或引入未预期行为。
/// 直接结束进程行为一致、可预期。</para>
///
/// <para><b>触发条件</b>（三者同时满足）：
/// <list type="number">
///   <item>总开关打开；</item>
///   <item>宽限期 &gt; 0（<b>为 0 表示不自动关闭，此时整体跳过本类全部逻辑</b>）；</item>
///   <item>当前模式是本地文件夹或 API（不是本地客户端模式）；</item>
/// </list>
/// 外加「列表中所有客户端均处于暂停状态」。</para>
///
/// <para><b>重置条件</b>：任一客户端恢复播放 / 切回本地客户端模式 / 进程自行退出。</para>
///
/// <para><b>读不到播放状态时不误杀</b>：<see cref="IClientPlaybackProbe"/> 返回
/// <see cref="ClientPlaybackState.Unknown"/> 时，本轮<b>跳过判定</b>（既不启动也不重置计时），
/// 避免把「探测失败」当成「已暂停」而误杀正在听的客户端。</para>
/// </summary>
public sealed class ClientLifecycleManager : IDisposable
{
    private readonly object _gate = new();
    private readonly List<TrackedClient> _clients = new();
    private readonly IClientPlaybackProbe _probe;
    private readonly Func<MusicClientPrefs> _prefsProvider;
    private readonly Func<MusicSourceMode> _modeProvider;

    // 计时起点（null = 未在计时）。用 Stopwatch 而非 DateTime 以免系统时间跳变影响。
    private Stopwatch? _idleTimer;

    /// <summary>轮询间隔（毫秒）。1 秒足够：宽限期最短也是分钟级。</summary>
    private const int PollIntervalMs = 1000;

    private readonly DispatcherTimer _timer;

    /// <summary>一个被启动器跟踪的客户端进程。</summary>
    private sealed class TrackedClient
    {
        public int Pid { get; init; }
        public string Name { get; init; } = "";
    }

    /// <summary>状态变化事件（供 UI 显示「将在 X 分钟后关闭客户端」）。</summary>
    public event Action<int, int>? GracefulCloseChanged;   // (剩余秒数, 客户端数)

    public ClientLifecycleManager(
        IClientPlaybackProbe probe,
        Func<MusicClientPrefs> prefsProvider,
        Func<MusicSourceMode> modeProvider)
    {
        _probe = probe;
        _prefsProvider = prefsProvider;
        _modeProvider = modeProvider;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PollIntervalMs) };
        _timer.Tick += OnTimerTick;
    }

    /// <summary>当前跟踪的客户端数（界面展示用）。</summary>
    public int TrackedCount
    {
        get { lock (_gate) return _clients.Count; }
    }

    /// <summary>剩余宽限期秒数；未在计时返回 -1。</summary>
    public int RemainingGraceSeconds
    {
        get
        {
            var prefs = _prefsProvider();
            if (!prefs.AutoCloseEnabled) return -1;
            var sw = _idleTimer;
            if (sw is null) return -1;
            var left = prefs.GraceMinutes * 60 - (int)sw.Elapsed.TotalSeconds;
            return left > 0 ? left : 0;
        }
    }

    public void Start() => _timer.Start();

    public void Stop()
    {
        _timer.Stop();
        lock (_gate) _idleTimer = null;
    }

    /// <summary>
    /// 登记一个由启动器拉起的客户端进程。
    /// 已在列表中（同 Pid）则忽略，避免重复登记导致 Kill 两次。
    /// </summary>
    public void Register(int pid, string? name = null)
    {
        if (pid <= 0) return;
        lock (_gate)
        {
            if (_clients.Any(c => c.Pid == pid)) return;
            _clients.Add(new TrackedClient
            {
                Pid = pid,
                Name = name ?? SafeProcessName(pid)
            });
            // 新客户端加入 → 重置计时（它可能正在播）
            _idleTimer = null;
        }
        Notify();
    }

    /// <summary>
    /// 移除一个进程（用户手动关掉了客户端）。按已确认事项：<b>不触发任何操作</b>，
    /// 只是从列表里去掉；剩余客户端若全暂停，计时继续按原节奏走。
    /// </summary>
    public void Unregister(int pid)
    {
        lock (_gate)
        {
            _clients.RemoveAll(c => c.Pid == pid);
        }
        Notify();
    }

    /// <summary>清空记录并停止计时（退出本地客户端模式时用）。</summary>
    public void ClearAll()
    {
        lock (_gate)
        {
            _clients.Clear();
            _idleTimer = null;
        }
        Notify();
    }

    /// <summary>
    /// 拉起一个本地客户端并登记。
    /// 用于「用户切回本地客户端模式时，若客户端未运行则启动」（规格实现项 2 最后一条）。
    /// </summary>
    public bool EnsureClientRunning(string exePath, string? args = null)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !System.IO.File.Exists(exePath)) return false;
        try
        {
            // 已在跟踪且进程仍活着 → 无需再起
            lock (_gate)
            {
                if (_clients.Any(c => IsAlive(c.Pid))) return true;
                _clients.RemoveAll(c => !IsAlive(c.Pid));
            }

            var psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,     // ★ Linux：客户端多为 ELF/AppImage，不经 shell
                WorkingDirectory = System.IO.Path.GetDirectoryName(exePath) ?? ""
            };
            if (!string.IsNullOrWhiteSpace(args)) psi.Arguments = args;

            var proc = Process.Start(psi);
            if (proc is null) return false;
            Register(proc.Id, proc.ProcessName);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>一轮判定。公开以便测试直接驱动（不依赖定时器）。</summary>
    public void Tick()
    {
        try
        {
            Step();
        }
        catch
        {
            // 任何异常都不得让定时器停摆；下一轮继续
        }
    }

    private void OnTimerTick(object? sender, EventArgs e) => Tick();

    private void Step()
    {
        var prefs = _prefsProvider();

        // ★ 宽限期为 0 → 完全跳过自动关闭逻辑（规格明确）
        if (!prefs.AutoCloseEnabled) { ResetTimer(); return; }

        // 总开关关闭 → 同样不该有关闭行为
        if (!prefs.Enabled) { ResetTimer(); return; }

        // 清理已自行退出的进程（已确认：自动移除，不触发任何操作）
        lock (_gate)
        {
            var before = _clients.Count;
            _clients.RemoveAll(c => !IsAlive(c.Pid));
            if (_clients.Count != before)
            {
                if (_clients.Count == 0) _idleTimer = null;
                Notify();
            }
            if (_clients.Count == 0) return;   // 没有客户端要管
        }

        // 切回本地客户端模式 → 重置（客户端本就该继续播）
        if (_modeProvider() == MusicSourceMode.LocalClient) { ResetTimer(); return; }

        // 读所有客户端的播放状态
        List<int> pids;
        lock (_gate) pids = _clients.Select(c => c.Pid).ToList();

        var states = _probe.GetStates(pids);

        // 读不到状态（探测失败）→ 本轮跳过，既不启动也不重置，避免误杀
        if (states is null || states.Count == 0) return;
        if (states.Values.Any(s => s == ClientPlaybackState.Unknown)) return;

        // 任一仍在播放 → 重置
        if (states.Values.Any(s => s == ClientPlaybackState.Playing)) { ResetTimer(); return; }

        // 全部暂停 → 启动/继续计时
        var sw = _idleTimer;
        if (sw is null)
        {
            sw = _idleTimer = Stopwatch.StartNew();
            Notify();
            return;
        }

        if (sw.Elapsed.TotalMinutes >= prefs.GraceMinutes)
        {
            CloseAllClients();
        }
        else
        {
            Notify();
        }
    }

    /// <summary>到期：Kill 列表中所有客户端进程并清空列表。</summary>
    private void CloseAllClients()
    {
        List<int> pids;
        lock (_gate)
        {
            pids = _clients.Select(c => c.Pid).ToList();
            _clients.Clear();
            _idleTimer = null;
        }

        foreach (var pid in pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.HasExited) continue;
                p.Kill();      // ★ 直接结束，不发优雅关闭（规格已确认）
            }
            catch
            {
                // 进程可能刚好自己退了，忽略
            }
        }
        Notify();
    }

    private void ResetTimer()
    {
        bool had;
        lock (_gate)
        {
            had = _idleTimer is not null;
            _idleTimer = null;
        }
        if (had) Notify();
    }

    private void Notify() => GracefulCloseChanged?.Invoke(RemainingGraceSeconds, TrackedCount);

    private static bool IsAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    private static string SafeProcessName(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.ProcessName; }
        catch { return "客户端"; }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
    }
}
