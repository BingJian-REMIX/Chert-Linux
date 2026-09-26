using System.Text;

namespace MCLCS.Core.Tokens;

/// <summary>挂机工作流指令类型。</summary>
public enum AfkOpKind
{
    /// <summary>F 功能键：<c>F&lt;n&gt;</c>，n ∈ [1,24]。例：F10。与「按键名称 E」区分，此处仅指 F1-F24。</summary>
    FunctionKey,
    /// <summary>延迟等待：<c>D&lt;秒&gt;</c>。例：D4 = 等待 4 秒。</summary>
    Delay,
    /// <summary>长按：<c>L&lt;秒&gt;</c>，作用于上一条按键指令。例：L3 = 长按 3 秒。</summary>
    LongPress,
    /// <summary>虚拟键码（数字）：<c>K&lt;code&gt;</c>，code ∈ [1,254]。例：K39 = 键码 39。</summary>
    KeyCode,
    /// <summary>连点：<c>C&lt;次数&gt;-&lt;间隔毫秒&gt;</c>。例：C1-500 = 每 500ms 点 1 次。</summary>
    Click,
    /// <summary>整体重复：<c>*&lt;次数&gt;</c>，0 表示无限循环。例：*0。</summary>
    Repeat,

    // ---- 扩展动作（Tier1，向后兼容旧 Token）----
    /// <summary>右键连点：<c>R&lt;次数&gt;-&lt;间隔毫秒&gt;</c>。例：R1-500。</summary>
    RightClick,
    /// <summary>鼠标相对移动：<c>M&lt;dx&gt;,&lt;dy&gt;</c>（像素，可负）。例：M100,0 = 右移 100px。</summary>
    MouseMove,
    /// <summary>滚轮：<c>S&lt;delta&gt;</c>（正=向下，负=向上）。例：S120。</summary>
    Scroll,
    /// <summary>输入文本：<c>T&lt;base64&gt;</c>，UTF-8 的 base64 编码。例：T开关。</summary>
    TypeText,
    /// <summary>按住键并保持（不弹起）：<c>G&lt;键码|名称&gt;</c>。须用 U 配对释放。</summary>
    KeyDown,
    /// <summary>松开键：<c>U&lt;键码|名称&gt;</c>，与 G 配对。</summary>
    KeyUp,
    /// <summary>随机等待：<c>J&lt;最大秒&gt;</c>，等待 0..最大秒 之间的随机时长（防踢）。</summary>
    RandomDelay,
    /// <summary>按名称按键（全键盘）：<c>E&lt;名称&gt;</c>，如 EA / EENTER / EUP。按下并抬起。</summary>
    NamedKey
}

/// <summary>单条挂机指令。</summary>
public sealed class AfkInstruction
{
    public AfkInstruction(AfkOpKind kind, int a, int b = 0, string? text = null)
    {
        Kind = kind;
        A = a;
        B = b;
        Text = text;
    }

    public AfkOpKind Kind { get; }
    public int A { get; }
    public int B { get; }
    /// <summary>文本/名称参数（TypeText 存 base64，NamedKey/G/U 可存按键名称）。</summary>
    public string? Text { get; }

    public string ToTokenPart() => Kind switch
    {
        AfkOpKind.FunctionKey => $"F{A}",
        AfkOpKind.Delay => $"D{A}",
        AfkOpKind.LongPress => $"L{A}",
        AfkOpKind.KeyCode => $"K{A}",
        AfkOpKind.Click => $"C{A}-{B}",
        AfkOpKind.Repeat => $"*{A}",
        AfkOpKind.RightClick => $"R{A}-{B}",
        AfkOpKind.MouseMove => $"M{A},{B}",
        AfkOpKind.Scroll => $"S{A}",
        AfkOpKind.TypeText => $"T{Text ?? ""}",
        AfkOpKind.KeyDown => $"G{(Text ?? A.ToString())}",
        AfkOpKind.KeyUp => $"U{(Text ?? A.ToString())}",
        AfkOpKind.RandomDelay => $"J{A}",
        AfkOpKind.NamedKey => $"E{Text ?? ""}",
        _ => ""
    };

    public string Describe() => Kind switch
    {
        AfkOpKind.FunctionKey => $"按下功能键 F{A}",
        AfkOpKind.Delay => $"等待 {A} 秒",
        AfkOpKind.LongPress => $"长按 {A} 秒",
        AfkOpKind.KeyCode => $"按下键码 {A}",
        AfkOpKind.Click => B > 0 ? $"连点 {A} 次，间隔 {B} 毫秒" : $"点击 {A} 次",
        AfkOpKind.Repeat => A == 0 ? "整体无限循环" : $"整体重复 {A} 次",
        AfkOpKind.RightClick => B > 0 ? $"右键连点 {A} 次，间隔 {B} 毫秒" : $"右键点击 {A} 次",
        AfkOpKind.MouseMove => $"鼠标移动 → 右{A}/下{B} 像素",
        AfkOpKind.Scroll => A >= 0 ? $"滚轮向下 {A}" : $"滚轮向上 {-A}",
        AfkOpKind.TypeText => $"输入文本：{(AfkWorkflowToken.DecodeText(Text) is var tt ? (string.IsNullOrEmpty(tt) ? "(空)" : tt) : "(空)")}",
        AfkOpKind.NamedKey => $"按下按键：{Text ?? ""}",
        AfkOpKind.KeyDown => !string.IsNullOrEmpty(Text) ? $"按住按键（{Text}）" : $"按住按键（键码 {A}）",
        AfkOpKind.KeyUp => !string.IsNullOrEmpty(Text) ? $"松开按键（{Text}）" : $"松开按键（键码 {A}）",
        AfkOpKind.RandomDelay => $"随机等待 ≤ {A} 秒",
        _ => "未知指令"
    };

    public override string ToString() => ToTokenPart();
}

/// <summary>解析结果。</summary>
public sealed class AfkParseResult
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public List<AfkInstruction> Instructions { get; init; } = new();
    public int RepeatCount { get; init; } = 1;
    public bool IsInfinite => Ok && Instructions.Any(i => i.Kind == AfkOpKind.Repeat) && RepeatCount == 0;
    public IEnumerable<AfkInstruction> Actions => Instructions.Where(i => i.Kind != AfkOpKind.Repeat);
    public static AfkParseResult Fail(string error) => new() { Ok = false, Error = error };
}

/// <summary>
/// 挂机工作流 Token 解析器。
/// <para>格式：分号分隔的指令序列。F=功能键、D=延时、L=长按、K=键码、C=连点、*=循环；
/// 扩展：R=右键连点、M=鼠标移动、S=滚轮、T=文本、G=按住、U=松开、J=随机等待、E=按名称按键。
/// 大小写不敏感，允许空白；<c>*</c> 只能出现一次且必须在最后。</para>
/// <para>G/U 既可写键码（G87）也可写按键名称（GW / EENTER），支持全键盘。</para>
/// </summary>
public static class AfkWorkflowToken
{
    public const int MaxFunctionKey = 24;
    public const int MaxDelaySeconds = 86400;
    public const int MaxLongPressSeconds = 3600;
    public const int MaxKeyCode = 254;
    public const int MaxClickCount = 10000;
    public const int MinClickIntervalMs = 10;
    public const int MaxClickIntervalMs = 600000;
    public const int MaxRepeat = 9999;
    public const int MaxInstructions = 128;

    public const int MinMouseDelta = -32768;
    public const int MaxMouseDelta = 32767;
    public const int MaxScrollDelta = 32767;
    public const int MaxRandomSeconds = 3600;
    public const int MaxTextBase64Length = 4000;
    public const int MaxNameLength = 32;

    public const string Sample = "F10;D4;L3;K39;C1-500;*0";

    /// <summary>按键名称 → 虚拟键码映射（大小写不敏感），用于 E/G/U 支持全键盘。</summary>
    private static readonly Dictionary<string, ushort> KeyNameToVk = new(StringComparer.OrdinalIgnoreCase)
    {
        // 字母
        {"A",0x41},{"B",0x42},{"C",0x43},{"D",0x44},{"E",0x45},{"F",0x46},{"G",0x47},{"H",0x48},{"I",0x49},
        {"J",0x4A},{"K",0x4B},{"L",0x4C},{"M",0x4D},{"N",0x4E},{"O",0x4F},{"P",0x50},{"Q",0x51},{"R",0x52},
        {"S",0x53},{"T",0x54},{"U",0x55},{"V",0x56},{"W",0x57},{"X",0x58},{"Y",0x59},{"Z",0x5A},
        // 数字
        {"0",0x30},{"1",0x31},{"2",0x32},{"3",0x33},{"4",0x34},{"5",0x35},{"6",0x36},{"7",0x37},{"8",0x38},{"9",0x39},
        // 功能键
        {"F1",0x70},{"F2",0x71},{"F3",0x72},{"F4",0x73},{"F5",0x74},{"F6",0x75},{"F7",0x76},{"F8",0x77},{"F9",0x78},{"F10",0x79},{"F11",0x7A},{"F12",0x7B},
        {"F13",0x7C},{"F14",0x7D},{"F15",0x7E},{"F16",0x7F},{"F17",0x80},{"F18",0x81},{"F19",0x82},{"F20",0x83},{"F21",0x84},{"F22",0x85},{"F23",0x86},{"F24",0x87},
        // 控制/编辑
        {"ENTER",0x0D},{"RETURN",0x0D},{"SPACE",0x20},{"TAB",0x09},{"BACKSPACE",0x08},{"BS",0x08},{"ESC",0x1B},{"ESCAPE",0x1B},
        {"CAPS",0x14},{"CAPSLOCK",0x14},{"PRINTSCREEN",0x2C},{"SCROLLLOCK",0x91},{"PAUSE",0x13},
        {"INSERT",0x2D},{"INS",0x2D},{"DELETE",0x2E},{"DEL",0x2E},{"HOME",0x24},{"END",0x23},
        {"PAGEUP",0x21},{"PGUP",0x21},{"PAGEDOWN",0x22},{"PGDN",0x22},
        {"UP",0x26},{"DOWN",0x28},{"LEFT",0x25},{"RIGHT",0x27},
        {"SHIFT",0x10},{"LSHIFT",0xA0},{"RSHIFT",0xA1},{"CTRL",0x11},{"CONTROL",0x11},{"LCTRL",0xA2},{"RCTRL",0xA3},
        {"ALT",0x12},{"LALT",0xA4},{"RALT",0xA5},{"WIN",0x5B},{"LWIN",0x5B},{"RWIN",0x5C},{"MENU",0x5D},
        // 符号
        {"MINUS",0xBD},{"EQUALS",0xBB},{"LBRACKET",0xDB},{"RBRACKET",0xDD},{"BACKSLASH",0xDC},
        {"SEMICOLON",0xBA},{"APOSTROPHE",0xDE},{"COMMA",0xBC},{"PERIOD",0xBE},{"SLASH",0xBF},{"BACKQUOTE",0xC0},
        // 小键盘
        {"NUM0",0x60},{"NUM1",0x61},{"NUM2",0x62},{"NUM3",0x63},{"NUM4",0x64},{"NUM5",0x65},{"NUM6",0x66},{"NUM7",0x67},{"NUM8",0x68},{"NUM9",0x69},
        {"NUMMULTIPLY",0x6A},{"NUMADD",0x6B},{"NUMSUBTRACT",0x6D},{"NUMDECIMAL",0x6E},{"NUMDIVIDE",0x6F},{"NUMLOCK",0x90},
    };

    /// <summary>编辑器下拉用：常用按键名称（有序）。</summary>
    public static readonly string[] CommonKeyNames =
    {
        "A","B","C","D","E","F","G","H","I","J","K","L","M","N","O","P","Q","R","S","T","U","V","W","X","Y","Z",
        "0","1","2","3","4","5","6","7","8","9",
        "ENTER","SPACE","TAB","BACKSPACE","ESC","CAPS",
        "UP","DOWN","LEFT","RIGHT","HOME","END","INSERT","DELETE","PAGEUP","PAGEDOWN",
        "SHIFT","CTRL","ALT","LSHIFT","RSHIFT","LCTRL","RCTRL","LALT","RALT","WIN",
        "F1","F2","F3","F4","F5","F6","F7","F8","F9","F10","F11","F12"
    };

    /// <summary>把按键名称或键码解析为虚拟键码；名称查表，纯数字视为键码。</summary>
    public static bool TryResolveVk(string? nameOrCode, out ushort vk)
    {
        vk = 0;
        if (string.IsNullOrWhiteSpace(nameOrCode)) return false;
        if (KeyNameToVk.TryGetValue(nameOrCode.Trim(), out var v)) { vk = v; return true; }
        if (int.TryParse(nameOrCode.Trim(), out var c) && c >= 1 && c <= 0xFFFF) { vk = (ushort)c; return true; }
        return false;
    }

    /// <summary>从指令取出要发送的虚拟键码（E/G/U 支持名称或键码）。</summary>
    public static ushort ResolveVk(AfkInstruction ins) =>
        ins.Text is not null ? (KeyNameToVk.TryGetValue(ins.Text, out var v) ? v : (ushort)0)
                             : (ushort)ins.A;

    public static AfkParseResult Parse(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return AfkParseResult.Fail("Token 为空");

        var parts = token.Split(';', StringSplitOptions.RemoveEmptyEntries)
                         .Select(p => p.Trim())
                         .Where(p => p.Length > 0)
                         .ToList();
        if (parts.Count == 0) return AfkParseResult.Fail("Token 不含任何指令");
        if (parts.Count > MaxInstructions) return AfkParseResult.Fail($"指令过多（上限 {MaxInstructions}）");

        var list = new List<AfkInstruction>();
        var repeat = 1;
        var repeatSeen = false;

        for (var i = 0; i < parts.Count; i++)
        {
            var p = parts[i];
            var head = char.ToUpperInvariant(p[0]);
            var body = p[1..].Trim();

            if (repeatSeen)
                return AfkParseResult.Fail("重复指令 * 必须位于末尾");

            switch (head)
            {
                case 'F':
                    if (!TryInt(body, 1, MaxFunctionKey, out var f))
                        return AfkParseResult.Fail($"第 {i + 1} 段 '{p}' 非法：F 后应为 1-{MaxFunctionKey}");
                    list.Add(new AfkInstruction(AfkOpKind.FunctionKey, f));
                    break;

                case 'D':
                    if (!TryInt(body, 0, MaxDelaySeconds, out var d))
                        return AfkParseResult.Fail($"第 {i + 1} 段 '{p}' 非法：D 后应为 0-{MaxDelaySeconds} 秒");
                    list.Add(new AfkInstruction(AfkOpKind.Delay, d));
                    break;

                case 'L':
                    if (!TryInt(body, 1, MaxLongPressSeconds, out var l))
                        return AfkParseResult.Fail($"第 {i + 1} 段 '{p}' 非法：L 后应为 1-{MaxLongPressSeconds} 秒");
                    if (list.Count == 0 || (list[^1].Kind != AfkOpKind.FunctionKey && list[^1].Kind != AfkOpKind.KeyCode))
                        return AfkParseResult.Fail($"第 {i + 1} 段 '{p}' 非法：长按必须紧跟在按键指令之后");
                    list.Add(new AfkInstruction(AfkOpKind.LongPress, l));
                    break;

                case 'K':
                    if (!TryInt(body, 1, MaxKeyCode, out var k))
                        return AfkParseResult.Fail($"第 {i + 1} 段 '{p}' 非法：K 后应为 1-{MaxKeyCode}");
                    list.Add(new AfkInstruction(AfkOpKind.KeyCode, k));
                    break;

                case 'C':
                {
                    var seg = body.Split('-', StringSplitOptions.RemoveEmptyEntries);
                    if (seg.Length != 2)
                        return AfkParseResult.Fail($"第 {i + 1} 段 '{p}' 非法：应为 C<次数>-<间隔毫秒>");
                    if (!TryInt(seg[0].Trim(), 1, MaxClickCount, out var cnt))
                        return AfkParseResult.Fail($"第 {i + 1} 段 '{p}' 非法：次数应为 1-{MaxClickCount}");
                    if (!TryInt(seg[1].Trim(), MinClickIntervalMs, MaxClickIntervalMs, out var iv))
                        return AfkParseResult.Fail($"第 {i + 1} 段 '{p}' 非法：间隔应为 {MinClickIntervalMs}-{MaxClickIntervalMs} 毫秒");
                    list.Add(new AfkInstruction(AfkOpKind.Click, cnt, iv));
                    break;
                }

                case 'R':
                {
                    var seg = body.Split('-', StringSplitOptions.RemoveEmptyEntries);
                    if (seg.Length != 2)
                        return AfkParseResult.Fail($"第 {i + 1} 段 '{p}' 非法：应为 R<次数>-<间隔毫秒>");
                    if (!TryInt(seg[0].Trim(), 1, MaxClickCount, out var rcnt))
                        return AfkParseResult.Fail($"第 {i + 1} 段 '{p}' 非法：次数应为 1-{MaxClickCount}");
                    if (!TryInt(seg[1].Trim(), MinClickIntervalMs, MaxClickIntervalMs, out var riv))
                        return AfkParseResult.Fail($"第 {i + 1} 段 '{p}' 非法：间隔应为 {MinClickIntervalMs}-{MaxClickIntervalMs} 毫秒");
                    list.Add(new AfkInstruction(AfkOpKind.RightClick, rcnt, riv));
                    break;
                }

                case 'M':
                {
                    var seg = body.Split(',', StringSplitOptions.RemoveEmptyEntries);
                    if (seg.Length != 2)
                        return AfkParseResult.Fail($"第 {i + 1} 段 '{p}' 非法：应为 M<dx>,<dy>");
                    if (!TryInt(seg[0].Trim(), MinMouseDelta, MaxMouseDelta, out var dx))
                        return AfkParseResult.Fail($"第 {i + 1} 段 '{p}' 非法：dx 应为 {MinMouseDelta}..{MaxMouseDelta}");
                    if (!TryInt(seg[1].Trim(), MinMouseDelta, MaxMouseDelta, out var dy))
                        return AfkParseResult.Fail($"第 {i + 1} 段 '{p}' 非法：dy 应为 {MinMouseDelta}..{MaxMouseDelta}");
                    list.Add(new AfkInstruction(AfkOpKind.MouseMove, dx, dy));
                    break;
                }

                case 'S':
                    if (!TryInt(body, MinMouseDelta, MaxScrollDelta, out var s))
                        return AfkParseResult.Fail($"第 {i + 1} 段 '{p}' 非法：S 后应为 {MinMouseDelta}..{MaxScrollDelta}");
                    list.Add(new AfkInstruction(AfkOpKind.Scroll, s));
                    break;

                case 'T':
                    if (body.Length > MaxTextBase64Length)
                        return AfkParseResult.Fail($"第 {i + 1} 段 '{p}' 非法：文本过长（base64 ≤ {MaxTextBase64Length}）");
                    list.Add(new AfkInstruction(AfkOpKind.TypeText, 0, 0, body));
                    break;

                case 'G':
                {
                    if (TryInt(body, 1, MaxKeyCode, out var g))
                        list.Add(new AfkInstruction(AfkOpKind.KeyDown, g));
                    else if (!string.IsNullOrWhiteSpace(body) && body.Length <= MaxNameLength)
                        list.Add(new AfkInstruction(AfkOpKind.KeyDown, 0, 0, body));
                    else
                        return AfkParseResult.Fail($"第 {i + 1} 段 '{p}' 非法：G 后应为 1-{MaxKeyCode} 或按键名称");
                    break;
                }

                case 'U':
                {
                    if (TryInt(body, 1, MaxKeyCode, out var u))
                        list.Add(new AfkInstruction(AfkOpKind.KeyUp, u));
                    else if (!string.IsNullOrWhiteSpace(body) && body.Length <= MaxNameLength)
                        list.Add(new AfkInstruction(AfkOpKind.KeyUp, 0, 0, body));
                    else
                        return AfkParseResult.Fail($"第 {i + 1} 段 '{p}' 非法：U 后应为 1-{MaxKeyCode} 或按键名称");
                    break;
                }

                case 'J':
                    if (!TryInt(body, 0, MaxRandomSeconds, out var j))
                        return AfkParseResult.Fail($"第 {i + 1} 段 '{p}' 非法：J 后应为 0-{MaxRandomSeconds} 秒");
                    list.Add(new AfkInstruction(AfkOpKind.RandomDelay, j));
                    break;

                case 'E':
                    if (string.IsNullOrWhiteSpace(body) || body.Length > MaxNameLength)
                        return AfkParseResult.Fail($"第 {i + 1} 段 '{p}' 非法：E 后应为按键名称（如 A/ENTER/UP）");
                    if (!KeyNameToVk.ContainsKey(body))
                        return AfkParseResult.Fail($"第 {i + 1} 段 '{p}' 非法：未知按键名称 '{body}'");
                    list.Add(new AfkInstruction(AfkOpKind.NamedKey, 0, 0, body));
                    break;

                case '*':
                    if (!TryInt(body, 0, MaxRepeat, out var r))
                        return AfkParseResult.Fail($"第 {i + 1} 段 '{p}' 非法：* 后应为 0-{MaxRepeat}（0=无限）");
                    list.Add(new AfkInstruction(AfkOpKind.Repeat, r));
                    repeat = r;
                    repeatSeen = true;
                    break;

                default:
                    return AfkParseResult.Fail($"第 {i + 1} 段 '{p}' 非法：未知指令 '{head}'");
            }
        }

        if (list.All(i => i.Kind == AfkOpKind.Repeat))
            return AfkParseResult.Fail("Token 不含任何动作指令");

        return new AfkParseResult { Ok = true, Instructions = list, RepeatCount = repeat };
    }

    public static bool IsValid(string? token) => Parse(token).Ok;

    public static string Serialize(IEnumerable<AfkInstruction> instructions) =>
        string.Join(";", instructions.Select(i => i.ToTokenPart()));

    public static string Describe(string? token)
    {
        var r = Parse(token);
        if (!r.Ok) return $"无效 Token：{r.Error}";

        var sb = new StringBuilder();
        var n = 1;
        foreach (var ins in r.Actions)
            sb.AppendLine($"{n++}. {ins.Describe()}");

        sb.Append(r.IsInfinite ? "循环：无限（手动停止）" : $"循环：{r.RepeatCount} 轮");
        return sb.ToString();
    }

    public static long EstimateCycleMs(string? token)
    {
        var r = Parse(token);
        if (!r.Ok) return 0;

        long total = 0;
        foreach (var ins in r.Actions)
        {
            total += ins.Kind switch
            {
                AfkOpKind.Delay => ins.A * 1000L,
                AfkOpKind.LongPress => ins.A * 1000L,
                AfkOpKind.Click => (long)ins.A * ins.B,
                AfkOpKind.RightClick => (long)ins.A * ins.B,
                AfkOpKind.MouseMove => 10,
                AfkOpKind.Scroll => 10,
                AfkOpKind.TypeText => (DecodeText(ins.Text)?.Length ?? 0) * 50L,
                AfkOpKind.NamedKey => 50,
                AfkOpKind.KeyDown => 10,
                AfkOpKind.KeyUp => 10,
                AfkOpKind.RandomDelay => ins.A * 1000L / 2,
                AfkOpKind.FunctionKey => 50,
                AfkOpKind.KeyCode => 50,
                _ => 0
            };
        }
        return total;
    }

    public static List<AfkInstruction> Expand(string? token, int maxCycles = 3)
    {
        var r = Parse(token);
        if (!r.Ok) return new List<AfkInstruction>();

        var cycles = r.IsInfinite ? Math.Max(1, maxCycles) : Math.Max(1, Math.Min(r.RepeatCount, maxCycles));
        var actions = r.Actions.ToList();
        var result = new List<AfkInstruction>(actions.Count * cycles);
        for (var c = 0; c < cycles; c++) result.AddRange(actions);
        return result;
    }

    public static string EncodeText(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text ?? ""));

    public static string? DecodeText(string? b64)
    {
        if (string.IsNullOrWhiteSpace(b64)) return "";
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(b64.Trim())); }
        catch { return null; }
    }

    public static bool HasUnbalancedHold(string? token)
    {
        var r = Parse(token);
        if (!r.Ok) return false;
        var g = r.Actions.Count(i => i.Kind == AfkOpKind.KeyDown);
        var u = r.Actions.Count(i => i.Kind == AfkOpKind.KeyUp);
        return g != u;
    }

    private static bool TryInt(string s, int min, int max, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        if (!int.TryParse(s, out var v)) return false;
        if (v < min || v > max) return false;
        value = v;
        return true;
    }
}
