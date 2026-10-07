namespace MCLCS.Core.Lan;

/// <summary>
/// 清单 #35 ~ #40：局域网联动的本地配置。
/// <para>默认**关闭**：发现服务会监听端口，属于改变系统网络姿态的行为，
/// 必须由用户在设置里显式打开。</para>
/// </summary>
public sealed class LanLinkConfig
{
    /// <summary>总开关。关闭时既不广播应答，也不监听指令。</summary>
    public bool Enabled { get; set; }

    /// <summary>对外显示的设备名；留空则用计算机名。</summary>
    public string DeviceName { get; set; } = "";

    /// <summary>发现端口（UDP）。</summary>
    public int DiscoveryPort { get; set; } = LanPeerProtocol.DiscoveryPort;

    /// <summary>指令端口（TCP）。</summary>
    public int CommandPort { get; set; } = LanPeerProtocol.CommandPort;

    /// <summary>
    /// 已配对设备发来的「邀请加入」是否免确认直接执行。
    /// 默认 false —— 任何来自局域网的指令都要在屏幕上确认一次。
    /// </summary>
    public bool AutoAcceptFromPaired { get; set; }

    /// <summary>已配对设备名（仅用于 UI 展示，真正的鉴权靠 token）。</summary>
    public List<string> KnownPeers { get; set; } = new();

    public static LanLinkConfig CreateDefault() => new();

    /// <summary>规范化：端口越界时回落到默认值。</summary>
    public LanLinkConfig Normalize()
    {
        if (DiscoveryPort is <= 0 or > 65535) DiscoveryPort = LanPeerProtocol.DiscoveryPort;
        if (CommandPort is <= 0 or > 65535) CommandPort = LanPeerProtocol.CommandPort;
        if (DiscoveryPort == CommandPort) CommandPort = LanPeerProtocol.CommandPort;
        KnownPeers ??= new List<string>();
        return this;
    }
}
