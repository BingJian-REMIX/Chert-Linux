using System.Collections.Generic;
using MCLCS.Core.Localization;

namespace MCLCS.Linux.App;

/// <summary>
/// 极简本地化层：把 Core.UI 视图模型里的 l10n key（如 <c>tab.game</c>）翻译为显示名。
/// <para>
/// 上游 MCLCS.App（WPF）的标题来自一套资源字典；MCLCS-Linux 不依赖 WPF，
/// 因此在此用一个静态字典桥接。未知 key 原样返回（不丢信息、不报错）。
/// </para>
/// </summary>
public static class Localization
{
    /// <summary>把 key 翻译为显示名；委托 Core 的 LocaleManager（zh_CN + en_US 完整框架）。
    /// 未知 key 原样返回，空 key 返回空串（保持与既有单测约定一致）。</summary>
    public static string Get(string? key) =>
        key is null ? "" : LocaleManager.T(key);

    /// <summary>副标签 Id → 对应的本地化 desc 键（<c>{id}.desc</c> 约定，部分 title 键与 Id 不同则显式映射）。</summary>
    private static readonly Dictionary<string, string> DescKeyMap = new()
    {
        // 下载页：Id 与 title 键一致（mod → tab.mods 需显式映射）
        ["minecraft"] = "tab.minecraft.desc",
        ["mod"] = "tab.mods.desc",
        ["shader"] = "tab.shader.desc",
        ["resourcepack"] = "tab.resourcepack.desc",
        ["modpack"] = "tab.modpack.desc",
        ["map"] = "tab.map.desc",
        // 局域网联动（下载页副标签，清单 #35 ~ #40）
        ["lanlink"] = "tab.lanlink.desc",
        ["center"] = "tool.dlcenter.desc",
        // 工具箱：Id 与 title 键一致（tool.{id}）
        ["log"] = "tool.log.desc",
        ["crash"] = "tool.crash.desc",
        ["perf"] = "tool.perf.desc",
        ["network"] = "tool.network.desc",
        ["datapack"] = "tool.datapack.desc",
        ["saves"] = "tool.saves.desc",
        ["backup"] = "tool.backup.desc",
        ["screenshot"] = "tool.screenshot.desc",
        ["clean"] = "tool.clean.desc",
        ["modpackio"] = "tool.modpackio.desc",
        ["music"] = "tool.music.desc",
        ["moddev"] = "tool.moddev.desc",
        ["packmaker"] = "tool.packmaker.desc",
        ["nbt"] = "tool.nbt.desc",
        ["command"] = "tool.command.desc",
        ["skin"] = "tool.skin.desc",
        ["shortcut"] = "tool.shortcut.desc",
        ["afk"] = "tool.afk.desc",
        ["aichat"] = "tool.aichat.desc",
        // 新增入口
        ["devtools"] = "tool.devtools.desc",
        // 设置页
        ["general"] = "settings.general.desc",
        ["launch"] = "settings.launch.desc",
        ["download"] = "settings.download.desc",
        ["recommend"] = "settings.recommend.desc",
        ["account"] = "settings.account.desc",
        ["ai"] = "settings.ai.desc",
        ["appearance"] = "settings.appearance.desc",
        ["about"] = "settings.about.desc",
    };

    /// <summary>副标签 Id → 一句话描述；走 Core.LocaleManager（zh_CN + en_US），未登记降级为「待接入 Core 能力」。</summary>
    public static string ToolDescription(string? id) =>
        id is null ? "" : (DescKeyMap.TryGetValue(id, out var key) ? LocaleManager.T(key) : "（待接入 Core 能力）");
}
