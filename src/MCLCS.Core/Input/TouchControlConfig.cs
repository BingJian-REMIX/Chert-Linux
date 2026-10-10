namespace MCLCS.Core.Input;

/// <summary>触屏按钮类型：方向键（长按持续移动）/ 动作键（按下即触发）/ 切换键（点一下锁定）。</summary>
public enum TouchButtonKind
{
    /// <summary>方向键：按住期间保持按下（W / A / S / D）。</summary>
    Direction,
    /// <summary>动作键：按下即触发一次（跳跃 / 潜行 / 攻击等）。</summary>
    Action,
    /// <summary>
    /// 切换键：轻点一下进入「按住」状态并保持，游戏内相当于该键一直压着；
    /// 再点一下才松开。用于需要长时间保持的键（默认布局里四向键正中的 Shift 潜行）。
    /// </summary>
    Toggle
}

/// <summary>清单 #11：触屏虚拟按键单项配置。</summary>
public sealed class TouchButtonConfig
{
    /// <summary>唯一 Id（删除 / 拖拽定位用）。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>按钮上显示的文字（如「前进」「跳跃」）。</summary>
    public string Label { get; set; } = "";

    /// <summary>虚拟键码（Win32 VK_*）。</summary>
    public int Vk { get; set; }

    /// <summary>对应按键的显示名（W / A / S / D / Space / Shift …）。</summary>
    public string KeyName { get; set; } = "";

    /// <summary>类型（方向键按住持续 / 动作键单次触发）。</summary>
    public TouchButtonKind Kind { get; set; } = TouchButtonKind.Action;

    /// <summary>面板内左上角 X 坐标（像素）。</summary>
    public double X { get; set; }

    /// <summary>面板内左上角 Y 坐标（像素）。</summary>
    public double Y { get; set; }

    /// <summary>按钮边长（像素，正方形；方向键与动作键一致）。</summary>
    public double Size { get; set; } = 58;

    public TouchButtonConfig Clone() => new()
    {
        Id = Id,
        Label = Label,
        Vk = Vk,
        KeyName = KeyName,
        Kind = Kind,
        X = X,
        Y = Y,
        Size = Size
    };
}

/// <summary>
/// 清单 #11：触屏模式配置。开启后随游戏进程显示一块虚拟按键面板，
/// 默认布局为 6 个等大方块：左侧十字四向键（W / A / S / D），
/// 十字正中是「点一下即锁定按住」的 Shift 潜行键，右侧一个 Space 跳跃键。
/// 其余按键可自行添加并拖动排版。
/// </summary>
public sealed class TouchControlConfig
{
    /// <summary>
    /// 默认布局的版本号。默认布局升级后会<b>自动</b>应用到老配置（见 <see cref="Normalize"/>），
    /// v2 = 六键方形 + 正中 Shift 切换键；v1 = 旧的四向 + 大圆跳跃键布局。
    /// </summary>
    public const int CurrentLayoutVersion = 2;

    /// <summary>布局版本。低于 <see cref="CurrentLayoutVersion"/> 时会被重置为当前默认布局。</summary>
    public int LayoutVersion { get; set; } = CurrentLayoutVersion;

    /// <summary>总开关。</summary>
    public bool Enabled { get; set; }

    /// <summary>面板不透明度（0.2 ~ 1）。</summary>
    public double Opacity { get; set; } = 0.72;

    /// <summary>面板宽度（像素）。</summary>
    public int PanelWidth { get; set; } = 420;

    /// <summary>面板高度（像素）。</summary>
    public int PanelHeight { get; set; } = 230;

    /// <summary>面板左上角屏幕坐标；小于 0 表示用默认位置（屏幕底部居中）。</summary>
    public int Left { get; set; } = -1;

    /// <summary>面板左上角屏幕坐标；小于 0 表示用默认位置（屏幕底部居中）。</summary>
    public int Top { get; set; } = -1;

    /// <summary>
    /// 是否让面板**跟随游戏窗口**（problem3）。
    /// 关闭时沿用既有行为：固定尺寸 + 自由拖动 + 记忆 <see cref="Left"/>/<see cref="Top"/>。
    /// 开启后面板自动贴合游戏窗口的左/右边缘，并随游戏窗口移动、缩放、切换最大化实时跟随；
    /// 此时 <see cref="Left"/>/<see cref="Top"/> 不再持久化（由跟随逻辑接管，避免互相打架）。
    /// </summary>
    public bool FollowGameWindow { get; set; }

    /// <summary>跟随时贴合游戏窗口的哪一侧（true = 左侧，false = 右侧）。</summary>
    public bool FollowGameWindowLeftSide { get; set; } = true;

    /// <summary>跟随时与游戏窗口边缘的水平间距（像素），避免面板压住窗口边框。</summary>
    public int FollowGameWindowMargin { get; set; } = 8;

    /// <summary>虚拟按键列表。</summary>
    public List<TouchButtonConfig> Buttons { get; set; } = new();

    /// <summary>
    /// 反序列化后调用：默认布局有升级时（或缺按键时）重置为当前默认布局。
    /// 用户在布局编辑器里改过的配置，版本号会被抬到当前值，因此不会被反复覆盖。
    /// </summary>
    public TouchControlConfig Normalize()
    {
        if (LayoutVersion < CurrentLayoutVersion)
        {
            // 只有完全没动过的「出厂原样」才自动换新；排版过的交给用户决定。
            if (!HasCustomizedLayout) Buttons = CreateDefault().Buttons;
            // 版本号无论如何都抬到当前值：表示这份配置已经过本次升级的处理
            LayoutVersion = CurrentLayoutVersion;
        }
        return this;
    }

    /// <summary>
    /// 是否为用户改动过的布局：按键序列与任一版「出厂布局」完全一致时视为没改过
    /// （只看 Vk 序列 —— 用户单纯改个显示名不该被当作自定义排版）。
    /// 自定义过的布局不做自动重置，避免覆盖用户排版；需要新版的用户点「恢复默认」即可。
    /// </summary>
    private bool HasCustomizedLayout =>
        FactoryLayouts.All(f => !f.SequenceEqual(Buttons.Select(b => b.Vk)));

    /// <summary>
    /// 各版出厂布局的按键序列（Vk 顺序与当版 <see cref="CreateDefault"/> 一致），用于判断布局是否被用户改过。
    /// </summary>
    private static readonly int[][] FactoryLayouts =
    {
        new[] { 0x57, 0x41, 0x53, 0x44, 0x20 },       // v1：四向 + 大圆跳跃键
        new[] { 0x57, 0x41, 0x10, 0x44, 0x53, 0x20 }  // v2：四向 + 正中 Shift 切换键 + 跳跃
    };

    /// <summary>
    /// 默认布局（v2）：左侧十字四向键 + 正中 Shift 切换键（点一下锁定潜行）+ 右侧 Space 跳跃键，
    /// 共 6 个等大方块。按 58px 方块 + 4px 间距排布，整体 182×182。
    /// </summary>
    public static TouchControlConfig CreateDefault() => new()
    {
        Enabled = false,
        Opacity = 0.72,
        PanelWidth = 420,
        PanelHeight = 200,
        Left = -1,
        Top = -1,
        LayoutVersion = CurrentLayoutVersion,
        Buttons = new List<TouchButtonConfig>
        {
            new() { Label = "前进", KeyName = "W", Vk = 0x57, Kind = TouchButtonKind.Direction, X = 62,  Y = 0,   Size = 58 },
            new() { Label = "左移", KeyName = "A", Vk = 0x41, Kind = TouchButtonKind.Direction, X = 0,   Y = 62,  Size = 58 },
            // 四向键正中：潜行。点一下即保持按住，再点解除。
            new() { Label = "潜行", KeyName = "Shift", Vk = 0x10, Kind = TouchButtonKind.Toggle, X = 62,  Y = 62,  Size = 58 },
            new() { Label = "右移", KeyName = "D", Vk = 0x44, Kind = TouchButtonKind.Direction, X = 124, Y = 62,  Size = 58 },
            new() { Label = "后退", KeyName = "S", Vk = 0x53, Kind = TouchButtonKind.Direction, X = 62,  Y = 124, Size = 58 },
            new() { Label = "跳跃", KeyName = "Space", Vk = 0x20, Kind = TouchButtonKind.Action, X = 300, Y = 62,  Size = 58 }
        }
    };

    public TouchControlConfig Clone()
    {
        var c = new TouchControlConfig
        {
            Enabled = Enabled,
            Opacity = Opacity,
            PanelWidth = PanelWidth,
            PanelHeight = PanelHeight,
            Left = Left,
            Top = Top,
            FollowGameWindow = FollowGameWindow,
            FollowGameWindowLeftSide = FollowGameWindowLeftSide,
            FollowGameWindowMargin = FollowGameWindowMargin,
            LayoutVersion = LayoutVersion
        };
        c.Buttons = Buttons.Select(b => b.Clone()).ToList();
        return c;
    }
}
