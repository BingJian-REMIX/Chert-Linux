# MCLCS-Linux

[MCLCS-WPF](https://cnb.cool/RLRS-Studio/MCLCS-WPF) 的 Linux 版本 —— 同一套 `MCLCS.Core` 引擎，Linux 原生界面（Avalonia）与命令行。

> 主库：https://cnb.cool/RLRS-Studio/MCLCS-Linux ｜ GitHub 镜像：https://github.com/BingJian-REMIX/MCLCS-Linux（本地经 ghproxy 直接推送，cnb CI 仅构建+测试）。

## 项目结构

```
MCLCS-Linux/
├── src/
│   ├── MCLCS.Core/          # 平台无关引擎（源自 MCLCS-WPF，vendor 复制维护）
│   │   ├── Launcher/        # 启动 / Java 探测 / 崩溃分析与修复
│   │   ├── Download/        # 下载 / 镜像策略 / Modrinth / 整合包
│   │   ├── Installers/      # Vanilla / Forge / Fabric / NeoForge / Quilt
│   │   ├── Auth/            # 微软 / 离线 / authlib-injector
│   │   ├── UI/              # UI 无关视图模型（四色主标签 / 侧边栏）
│   │   └── ...              # Mods / Save / Servers / Statistics / Toolbox 等
│   └── MCLCS.Linux.App/     # Linux GUI（Avalonia 11，net10.0）
├── tools/
│   └── MCLCS.Linux.Cli/     # 命令行 mclcs（仅引用 Core，替代上游 WPF 依赖版 CLI）
└── tests/
    └── MCLCS.Linux.Tests/   # Core 桥接测试（Linux 平台验证）
```

## 技术栈

| 项 | 选择 | 说明 |
|---|---|---|
| 引擎层 | `MCLCS.Core`（vendor） | 与 MCLCS-WPF 同源；零第三方依赖，纯 BCL |
| GUI | Avalonia 11 | WPF 的跨平台继任者，最大化界面/功能同步 |
| CLI | `mclcs` | net10.0，仅引用 Core |
| 目标框架 | net10.0 | 全工程统一 |

## 构建与运行

需要 .NET 10 SDK。

```bash
# 编译全部
dotnet build MCLCS-Linux.sln -c Release

# 跑测试
dotnet test tests/MCLCS.Linux.Tests -c Release

# CLI 冒烟
dotnet run --project tools/MCLCS.Linux.Cli -c Release -- detect-java
dotnet run --project tools/MCLCS.Linux.Cli -c Release -- tabs
dotnet run --project tools/MCLCS.Linux.Cli -c Release -- sidebar toolbox

# GUI（需桌面环境 X11/Wayland）
dotnet run --project src/MCLCS.Linux.App -c Release
```

> NuGet 源使用华为云镜像（见 `nuget.config`），国内网络亦可还原。

## 与 MCLCS-WPF 的同步策略

- **Core 采用 vendor 复制**（`src/MCLCS.Core` 独立维护）。上游 Core 更新时手动同步合并。
- 已在本仓库内完成的 Linux 适配：
  - `JavaDetector.AddRegistryJava`、`ShortcutGenerator.TryCreateLnk` 加 `[SupportedOSPlatform("windows")]`，Linux 下经 `IsWindows()` 守卫自然跳过，注册表/WSH COM 不触发。
  - Linux Java 探测路径：`JAVA_HOME` / `/usr/lib/jvm` / `/opt/java` / PATH（Core 原生支持）。
  - 快捷方式走 `.desktop`（Core 原生支持）。
- UI 层不复用 WPF 代码，直接绑定 Core 的 `MainTabs` / `Sidebar` / `SidebarState` / `TabThemeConfig` / `ObservableObject`，保证四色标签、侧边栏结构与上游规格一致。

## 上游已知漂移（以实际代码为准，2026-08 验证）

| 上游工程 | 实测状态 |
|---|---|
| `MCLCS.App`（WPF） | `net8.0-windows`，纯 Windows，Linux 不编译（预期内） |
| `MCLCS.Cli` | 目标 `net8.0-windows` 且引用 `MCLCS.App`，Linux 不可用 → 本仓库以 `MCLCS.Linux.Cli` 替代 |
| `MCLCS.SelfCheck` | 编译失败：`LauncherUpdater.CheckAsync` 调用 3 参，Core 实际签名 2 参（API 漂移） |
| `MCLCS.Core` | ✅ Linux 编译零警告，47 项上游测试全过 |

## 开发阶段说明

当前为**开发阶段**（v0.x）：

- ✅ Core vendor 落地 + Linux 平台注解
- ✅ CLI 可用（detect-java / tabs / sidebar）
- ✅ Avalonia 骨架（四色标签 + 侧边栏 + Java 检测面板）
- ✅ 测试 10/10 绿
- 🚧 各功能页逐步对齐 Core 能力（下载 / 安装 / 启动 / 工具箱 20 项 …）

## 双仓库同步（cnb 主库 + GitHub 镜像）

- **cnb 主库**：`https://cnb.cool/RLRS-Studio/MCLCS-Linux`（`main` 分支，CI 仅做构建+测试）。
- **GitHub 镜像**：`https://github.com/BingJian-REMIX/MCLCS-Linux`（由本地经 ghproxy 直接推送，
  cnb CI 不再负责镜像）。

本地双推（可选）：`./push-all.sh --setup <cnb-url> <github-url>` 配置一次，
之后每次 `./push-all.sh` 同时推送两个远端。

## 许可

随上游 MCLCS-WPF（见上游仓库声明）。
