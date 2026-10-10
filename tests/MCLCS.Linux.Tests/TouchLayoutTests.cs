using System.Linq;
using MCLCS.Core.Input;
using Xunit;

namespace MCLCS.Core.Tests;

/// <summary>
/// 触屏虚拟按键面板的默认布局约定（清单 #11）。
/// 这里锁的是「出厂必须是什么样」与「升级时会不会踩到用户的自定义排版」两条：
/// 前者决定了第一次打开面板的观感，后者一旦写错，用户的排版会在更新后被悄悄冲掉。
/// </summary>
public class TouchLayoutTests
{
    [Fact]
    public void CreateDefault_IsSixEqualSquares()
    {
        var cfg = TouchControlConfig.CreateDefault();

        Assert.Equal(6, cfg.Buttons.Count);
        // 全部同一尺寸才是「6 个方形」，混进一个放大的圆形跳跃键就不对了
        Assert.Single(cfg.Buttons.Select(b => b.Size).Distinct());
        Assert.All(cfg.Buttons, b => Assert.True(b.Size > 0));
    }

    [Fact]
    public void CreateDefault_PutsSneakToggleAtDpadCenter()
    {
        var cfg = TouchControlConfig.CreateDefault();

        var center = cfg.Buttons.SingleOrDefault(b => b.Kind == TouchButtonKind.Toggle);
        Assert.NotNull(center);
        Assert.Equal(0x10, center!.Vk);            // VK_SHIFT
        Assert.Equal("Shift", center.KeyName);

        // 十字四向键围出的中心格：左右两侧 A / D 的中点，上下两侧 W / S 的中点
        var dpad = cfg.Buttons.Where(b => b.Kind == TouchButtonKind.Direction).ToList();
        Assert.Equal(4, dpad.Count);
        Assert.Equal(dpad.Single(b => b.KeyName == "A").Y, center.Y);
        Assert.Equal(dpad.Single(b => b.KeyName == "W").X, center.X);

        // 剩余那个是跳跃键（Space），与中心格不重叠
        var jump = cfg.Buttons.Single(b => b.Kind == TouchButtonKind.Action);
        Assert.Equal(0x20, jump.Vk);
        Assert.True(jump.X >= center.X + center.Size);
    }

    [Fact]
    public void Normalize_UpgradesUntouchedV1Layout()
    {
        // 老版本出厂布局：四向 + 一个大圆跳跃键，无 Toggle
        var v1 = TouchControlConfig.CreateDefault();
        v1.Buttons = new System.Collections.Generic.List<TouchButtonConfig>
        {
            new() { Label = "前进", KeyName = "W", Vk = 0x57, Kind = TouchButtonKind.Direction, Size = 58 },
            new() { Label = "左移", KeyName = "A", Vk = 0x41, Kind = TouchButtonKind.Direction, Size = 58 },
            new() { Label = "后退", KeyName = "S", Vk = 0x53, Kind = TouchButtonKind.Direction, Size = 58 },
            new() { Label = "右移", KeyName = "D", Vk = 0x44, Kind = TouchButtonKind.Direction, Size = 58 },
            new() { Label = "跳跃", KeyName = "Space", Vk = 0x20, Kind = TouchButtonKind.Action, Size = 78 }
        };
        v1.LayoutVersion = 1;

        var result = v1.Normalize();

        Assert.Equal(TouchControlConfig.CurrentLayoutVersion, result.LayoutVersion);
        Assert.Equal(6, result.Buttons.Count);
        Assert.Contains(result.Buttons, b => b.Kind == TouchButtonKind.Toggle);
    }

    [Fact]
    public void Normalize_KeepsCustomizedLayout()
    {
        var cfg = TouchControlConfig.CreateDefault();
        cfg.Buttons.Add(new TouchButtonConfig
        {
            Label = "调试", KeyName = "F3", Vk = 0x72, Kind = TouchButtonKind.Action, Size = 58
        });
        cfg.LayoutVersion = 1;

        cfg.Normalize();

        // 用户自己加过键的排版不能被出厂布局覆盖（要新版得点「恢复默认」）
        Assert.Equal(7, cfg.Buttons.Count);
        Assert.Contains(cfg.Buttons, b => b.KeyName == "F3");
        Assert.Equal(TouchControlConfig.CurrentLayoutVersion, cfg.LayoutVersion);
    }
}
