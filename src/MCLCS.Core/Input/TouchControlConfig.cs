namespace MCLCS.Core.Input;

/// <summary>触屏按钮类型：方向键（长按持续移动）/ 动作键（按下即触发）。</summary>
public enum TouchButtonKind
{
    /// <summary>方向键：按住期间保持按下（W / A / S / D）。</summary>
    Direction,
    /// <summary>动作键：按下即触发一次（跳跃 / 潜行 / 攻击等）。</summary>
    Action
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
/// 默认提供虚拟方向键（W / A / S / D）与跳跃键（Space），其余按键可自行添加并拖动排版。
/// </summary>
public sealed class TouchControlConfig
{
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
    /// 默认布局：左侧十字方向键（W 上 / A 左 / S 下 / D 右）+ 右侧跳跃键（Space）。
    /// </summary>
    public static TouchControlConfig CreateDefault() => new()
    {
        Enabled = false,
        Opacity = 0.72,
        PanelWidth = 420,
        PanelHeight = 230,
        Left = -1,
        Top = -1,
        Buttons = new List<TouchButtonConfig>
        {
            new() { Label = "前进", KeyName = "W", Vk = 0x57, Kind = TouchButtonKind.Direction, X = 62, Y = 8,  Size = 58 },
            new() { Label = "左移", KeyName = "A", Vk = 0x41, Kind = TouchButtonKind.Direction, X = 0,  Y = 66, Size = 58 },
            new() { Label = "后退", KeyName = "S", Vk = 0x53, Kind = TouchButtonKind.Direction, X = 62, Y = 124, Size = 58 },
            new() { Label = "右移", KeyName = "D", Vk = 0x44, Kind = TouchButtonKind.Direction, X = 124, Y = 66, Size = 58 },
            new() { Label = "跳跃", KeyName = "Space", Vk = 0x20, Kind = TouchButtonKind.Action, X = 300, Y = 78, Size = 78 }
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
            Top = Top
        };
        c.Buttons = Buttons.Select(b => b.Clone()).ToList();
        return c;
    }
}
