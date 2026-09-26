using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MCLCS.Core.Tokens;

namespace MCLCS.Linux.App.Services;

/// <summary>AFK 运行进度快照（供 UI 展示当前轮次 / 步骤 / 已用时长）。与 WPF 端结构一致。</summary>
public sealed class AfkRunProgress
{
    public int StepIndex { get; init; }
    public int TotalSteps { get; init; }
    public string CurrentStep { get; init; } = "";
    public int Cycle { get; init; }
    public int TotalCycles { get; init; }
    public TimeSpan Elapsed { get; init; }
    public bool Running { get; init; }
}

/// <summary>
/// AFK 工作流执行引擎（Linux 版，对齐 WPF <c>AfkRunner</c>）：把 <see cref="AfkWorkflowToken"/>
/// 解析出的宏指令，通过 <c>xdotool</c>（X11 / XWayland）派发到目标窗口（默认正在运行的 MC 实例）。
/// 支持延迟 / 长按 / 连点 / 整体循环 / 右键连点 / 鼠标移动 / 滚轮 / 文本输入 / 按住-松开 / 随机等待，并可随时取消。
/// 停止时强制释放所有「按住」的键，避免 MC 卡在持续前进 / 按下状态。
/// <para>Wayland 原生会话需改用 ydotool；本实现优先 xdotool（项目 MainWindow 已依赖它取分辨率）。</para>
/// </summary>
public static class AfkRunner
{
    /// <summary>键盘按键名称 → xdotool keysym 名称（大小写不敏感）。覆盖 Core.KeyNameToVk 的全部规范名。</summary>
    private static readonly Dictionary<string, string> NameToXdotool = new(StringComparer.OrdinalIgnoreCase)
    {
        // 字母 / 数字（xdotool 用小写字母键名）
        {"A","a"},{"B","b"},{"C","c"},{"D","d"},{"E","e"},{"F","f"},{"G","g"},{"H","h"},{"I","i"},
        {"J","j"},{"K","k"},{"L","l"},{"M","m"},{"N","n"},{"O","o"},{"P","p"},{"Q","q"},{"R","r"},
        {"S","s"},{"T","t"},{"U","u"},{"V","v"},{"W","w"},{"X","x"},{"Y","y"},{"Z","z"},
        {"0","0"},{"1","1"},{"2","2"},{"3","3"},{"4","4"},{"5","5"},{"6","6"},{"7","7"},{"8","8"},{"9","9"},
        // 功能键
        {"F1","F1"},{"F2","F2"},{"F3","F3"},{"F4","F4"},{"F5","F5"},{"F6","F6"},{"F7","F7"},{"F8","F8"},
        {"F9","F9"},{"F10","F10"},{"F11","F11"},{"F12","F12"},{"F13","F13"},{"F14","F14"},{"F15","F15"},
        {"F16","F16"},{"F17","F17"},{"F18","F18"},{"F19","F19"},{"F20","F20"},{"F21","F21"},{"F22","F22"},
        {"F23","F23"},{"F24","F24"},
        // 控制 / 编辑
        {"ENTER","Return"},{"RETURN","Return"},{"SPACE","space"},{"TAB","Tab"},
        {"BACKSPACE","BackSpace"},{"BS","BackSpace"},{"ESC","Escape"},{"ESCAPE","Escape"},
        {"CAPS","Caps_Lock"},{"CAPSLOCK","Caps_Lock"},{"PRINTSCREEN","Print"},
        {"SCROLLLOCK","Scroll_Lock"},{"PAUSE","Pause"},
        {"INSERT","Insert"},{"INS","Insert"},{"DELETE","Delete"},{"DEL","Delete"},
        {"HOME","Home"},{"END","End"},{"PAGEUP","Page_Up"},{"PGUP","Page_Up"},
        {"PAGEDOWN","Page_Down"},{"PGDN","Page_Down"},
        {"UP","Up"},{"DOWN","Down"},{"LEFT","Left"},{"RIGHT","Right"},
        {"SHIFT","Shift"},{"LSHIFT","Shift_L"},{"RSHIFT","Shift_R"},
        {"CTRL","Control"},{"CONTROL","Control"},{"LCTRL","Control_L"},{"RCTRL","Control_R"},
        {"ALT","Alt"},{"LALT","Alt_L"},{"RALT","Alt_R"},
        {"WIN","Super_L"},{"LWIN","Super_L"},{"RWIN","Super_R"},{"MENU","Menu"},
        // 符号
        {"MINUS","minus"},{"EQUALS","equal"},{"LBRACKET","bracketleft"},{"RBRACKET","bracketright"},
        {"BACKSLASH","backslash"},{"SEMICOLON","semicolon"},{"APOSTROPHE","apostrophe"},
        {"COMMA","comma"},{"PERIOD","period"},{"SLASH","slash"},{"BACKQUOTE","grave"},
        // 小键盘
        {"NUM0","KP_0"},{"NUM1","KP_1"},{"NUM2","KP_2"},{"NUM3","KP_3"},{"NUM4","KP_4"},{"NUM5","KP_5"},
        {"NUM6","KP_6"},{"NUM7","KP_7"},{"NUM8","KP_8"},{"NUM9","KP_9"},
        {"NUMMULTIPLY","KP_Multiply"},{"NUMADD","KP_Add"},{"NUMSUBTRACT","KP_Subtract"},
        {"NUMDECIMAL","KP_Decimal"},{"NUMDIVIDE","KP_Divide"},{"NUMLOCK","Num_Lock"},
    };

    /// <summary>把虚拟键码（数字）映射为 xdotool keysym；覆盖常用字母 / 数字 / 功能键 / 修饰键。</summary>
    private static string VkToXdotool(int vk)
    {
        if (vk is >= 0x30 and <= 0x39) return ((char)vk).ToString();          // 数字
        if (vk is >= 0x41 and <= 0x5A) return ((char)(vk - 0x41 + 'a')).ToString(); // 字母 → 小写
        if (vk is >= 0x70 and <= 0x87) return $"F{vk - 0x70 + 1}";             // F1..F24
        if (vk is >= 0x60 and <= 0x69) return $"KP_{vk - 0x60}";                // 小键盘数字
        return vk switch
        {
            0x08 => "BackSpace", 0x09 => "Tab", 0x0D => "Return",
            0x10 => "Shift", 0x11 => "Control", 0x12 => "Alt", 0x13 => "Pause",
            0x14 => "Caps_Lock", 0x1B => "Escape", 0x20 => "space",
            0x21 => "Page_Up", 0x22 => "Page_Down", 0x23 => "End", 0x24 => "Home",
            0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down",
            0x2C => "Print", 0x2D => "Insert", 0x2E => "Delete",
            0x5B => "Super_L", 0x5C => "Super_R", 0x5D => "Menu",
            0x90 => "Num_Lock", 0x91 => "Scroll_Lock",
            0xA0 => "Shift_L", 0xA1 => "Shift_R", 0xA2 => "Control_L", 0xA3 => "Control_R",
            0xA4 => "Alt_L", 0xA5 => "Alt_R",
            0xBA => "semicolon", 0xBB => "equal", 0xBC => "comma", 0xBD => "minus",
            0xBE => "period", 0xBF => "slash", 0xC0 => "grave",
            0xDB => "bracketleft", 0xDC => "backslash", 0xDD => "bracketright", 0xDE => "apostrophe",
            0x6A => "KP_Multiply", 0x6B => "KP_Add", 0x6D => "KP_Subtract",
            0x6E => "KP_Decimal", 0x6F => "KP_Divide",
            _ => $"0x{vk:X2}"
        };
    }

    /// <summary>运行一段 AFK Token。<paramref name="targetPid"/> 为 MC 进程 PID（为空则用当前前台窗口）。</summary>
    public static async Task RunAsync(string token, int? targetPid, IProgress<AfkRunProgress>? progress, CancellationToken ct)
    {
        if (!XdotoolAvailable())
            throw new InvalidOperationException("未找到 xdotool。请在系统安装 xdotool（X11 / XWayland）后再使用挂机功能：sudo apt install xdotool");

        var result = AfkWorkflowToken.Parse(token);
        if (!result.Ok)
            throw new InvalidOperationException(result.Error ?? "Token 非法");

        // 尽量把输入焦点切到目标 MC 窗口（失败则落到当前前台窗口）
        if (targetPid.HasValue)
        {
            try { RunXdotool($"search --pid {targetPid.Value} windowactivate --sync %1", ct); }
            catch { /* 忽略：可能窗口尚未就绪，回落到前台窗口 */ }
            await Task.Delay(150, ct);
        }

        var actions = result.Actions.ToList();
        var totalCycles = result.IsInfinite ? int.MaxValue : Math.Max(1, result.RepeatCount);
        var sw = Stopwatch.StartNew();
        var cycle = 0;
        var held = new HashSet<string>();

        try
        {
            do
            {
                cycle++;
                for (var i = 0; i < actions.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var ins = actions[i];
                    progress?.Report(new AfkRunProgress
                    {
                        StepIndex = i + 1,
                        TotalSteps = actions.Count,
                        CurrentStep = ins.Describe(),
                        Cycle = cycle,
                        TotalCycles = result.IsInfinite ? 0 : result.RepeatCount,
                        Elapsed = sw.Elapsed,
                        Running = true
                    });
                    await ExecuteAsync(ins, actions, i, ct, held);
                }
            } while (cycle < totalCycles && !ct.IsCancellationRequested);
        }
        finally
        {
            // 关键：无论正常结束还是取消，强制释放所有「按住」的键，避免卡键。
            foreach (var key in held) RunXdotool($"keyup {key}", ct);
            held.Clear();
        }
    }

    private static async Task ExecuteAsync(AfkInstruction ins, List<AfkInstruction> actions, int index, CancellationToken ct, HashSet<string> held)
    {
        switch (ins.Kind)
        {
            case AfkOpKind.Delay:
                await Task.Delay(Math.Max(0, ins.A) * 1000, ct);
                break;

            case AfkOpKind.FunctionKey:
                RunXdotool($"key {ToXdotoolKey(ins)}", ct);
                await Task.Delay(40, ct);
                break;

            case AfkOpKind.KeyCode:
                RunXdotool($"key {ToXdotoolKey(ins)}", ct);
                await Task.Delay(40, ct);
                break;

            case AfkOpKind.LongPress:
            {
                // 长按作用于上一条按键 / 功能键指令
                var prev = actions.Take(index)
                    .LastOrDefault(x => x.Kind is AfkOpKind.FunctionKey or AfkOpKind.KeyCode or AfkOpKind.NamedKey);
                var key = prev is not null ? ToXdotoolKey(prev) : "";
                if (string.IsNullOrEmpty(key))
                {
                    await Task.Delay(ins.A * 1000, ct); // 没有可长按的键：退化为等待
                    break;
                }
                RunXdotool($"keydown {key}", ct);
                try { await Task.Delay(ins.A * 1000, ct); }
                finally { RunXdotool($"keyup {key}", ct); }
                break;
            }

            case AfkOpKind.Click:
                await ClickAsync(ins, left: true, ct);
                break;

            case AfkOpKind.RightClick:
                await ClickAsync(ins, left: false, ct);
                break;

            case AfkOpKind.MouseMove:
                RunXdotool($"mousemove_relative {ins.A} {ins.B}", ct);
                await Task.Delay(10, ct);
                break;

            case AfkOpKind.Scroll:
            {
                // xdotool 无直接滚轮命令，用 click 4(上)/5(下) 模拟，每 120 为一格。
                var notches = Math.Max(1, Math.Abs(ins.A) / 120);
                var button = ins.A >= 0 ? 5 : 4; // 正=向下
                for (var n = 0; n < notches; n++)
                {
                    ct.ThrowIfCancellationRequested();
                    RunXdotool($"click {button}", ct);
                    await Task.Delay(10, ct);
                }
                break;
            }

            case AfkOpKind.TypeText:
            {
                var text = AfkWorkflowToken.DecodeText(ins.Text);
                if (!string.IsNullOrEmpty(text))
                    await TypeTextAsync(text, ct);
                break;
            }

            case AfkOpKind.NamedKey:
            {
                var key = ToXdotoolKey(ins);
                if (!string.IsNullOrEmpty(key))
                {
                    RunXdotool($"key {key}", ct);
                    await Task.Delay(40, ct);
                }
                break;
            }

            case AfkOpKind.KeyDown:
            {
                var key = ToXdotoolKey(ins);
                if (!string.IsNullOrEmpty(key)) { RunXdotool($"keydown {key}", ct); held.Add(key); }
                break;
            }

            case AfkOpKind.KeyUp:
            {
                var key = ToXdotoolKey(ins);
                if (!string.IsNullOrEmpty(key)) { RunXdotool($"keyup {key}", ct); held.Remove(key); }
                break;
            }

            case AfkOpKind.RandomDelay:
            {
                var ms = ins.A <= 0 ? 0 : Random.Shared.Next(0, ins.A * 1000);
                await Task.Delay(ms, ct);
                break;
            }

            default:
                break;
        }
    }

    private static async Task ClickAsync(AfkInstruction ins, bool left, CancellationToken ct)
    {
        const int holdMs = 20;
        var button = left ? 1 : 3;
        for (var c = 0; c < ins.A; c++)
        {
            ct.ThrowIfCancellationRequested();
            RunXdotool($"click {button}", ct);
            if (ins.B > holdMs)
                await Task.Delay(ins.B - holdMs, ct);
            else
                await Task.Delay(holdMs, ct);
        }
    }

    // 用临时文件 + `xdotool type --file` 避免命令行转义问题（任意字符包括中文/空格都安全）。
    private static async Task TypeTextAsync(string text, CancellationToken ct)
    {
        string? tmp = null;
        try
        {
            tmp = Path.GetTempFileName();
            await File.WriteAllTextAsync(tmp, text, new UTF8Encoding(false), ct);
            RunXdotool($"type --file {tmp}", ct);
        }
        finally
        {
            if (tmp is not null) { try { File.Delete(tmp); } catch { } }
        }
    }

    private static string ToXdotoolKey(AfkInstruction ins)
    {
        if (ins.Kind == AfkOpKind.FunctionKey) return $"F{ins.A}";
        if (ins.Text is not null)
            return NameToXdotool.TryGetValue(ins.Text, out var n) ? n : ins.Text.ToLowerInvariant();
        return VkToXdotool(ins.A);
    }

    private static bool XdotoolAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo("xdotool", "version")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(3000);
            return p.ExitCode == 0 || p.ExitCode == 1; // xdotool version 可能返回 1 但仍可用
        }
        catch
        {
            return false;
        }
    }

    /// <summary>执行一条 xdotool 命令；返回退出码。出错（未安装 / 超时）时抛出。</summary>
    private static int RunXdotool(string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("xdotool", args)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!p.Start())
            throw new InvalidOperationException("无法启动 xdotool 进程");

        var err = new StringBuilder();
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) err.AppendLine(e.Data); };
        p.BeginErrorReadLine();

        // 不阻塞取消：超时则杀掉，交由上层抛出 OperationCanceledException
        using (ct.Register(() => { try { p.Kill(); } catch { } }))
        {
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(); } catch { }
                ct.ThrowIfCancellationRequested();
                throw new InvalidOperationException("xdotool 执行超时");
            }
        }
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"xdotool 失败（{p.ExitCode}）：{err}");
        return p.ExitCode;
    }
}
