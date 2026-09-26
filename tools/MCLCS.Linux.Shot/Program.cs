using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MCLCS.Linux.App;
using MCLCS.Linux.App.Controls;
using MCLCS.Linux.App.Services;
using MCLCS.Linux.App.ViewModels;
using MCLCS.Linux.App.Views;
using MCLCS.Linux.App.Views.Pages;
using MCLCS.Core.Profiles;
using MCLCS.Core.Skin;
using MCLCS.Core.Theme;
using MCLCS.Core.Toolbox;
using MCLCS.Core.Save;
using MCLCS.Core.UI;
using MCLCS.Core.Utils;
using SkiaSharp;

namespace MCLCS.Linux.Shot;

/// <summary>
/// 全量截屏工程：加载真实 App + 真实 MainWindow（含侧栏 / 标签 / 状态栏壳层），
/// 经 NavigateTo 路由到全部主标签与副页逐一截图。
/// 覆盖：Game×1 + Download×6 + Toolbox×20 + Settings×8 + skin3d 直出 + UI 组件×2。
/// 用法：MCLCS.Linux.Shot &lt;输出目录&gt;（默认 /workspace/shots）
/// </summary>
internal static class Program
{
    private const int W = 1600, H = 1000;

    // 皮肤预览区尺寸（运行时由皮肤页的 SkinPreview3D.Bounds 填充，供 skin3d 直出精确匹配投影）
    private static Avalonia.Size _previewSize = new(480, 640);

    // (主标签, 侧栏 id, 文件名)
    private static readonly (MainTabKind Kind, string Sid, string Name)[] Nav =
    {
        (MainTabKind.Game, "", "home"),

        (MainTabKind.Download, "minecraft", "dl-minecraft"),
        (MainTabKind.Download, "mod", "dl-mod"),
        (MainTabKind.Download, "shader", "dl-shader"),
        (MainTabKind.Download, "resourcepack", "dl-resourcepack"),
        (MainTabKind.Download, "modpack", "dl-modpack"),
        (MainTabKind.Download, "map", "dl-map"),

        (MainTabKind.Toolbox, "log", "tb-log"),
        (MainTabKind.Toolbox, "clean", "tb-clean"),
        (MainTabKind.Toolbox, "backup", "tb-backup"),
        (MainTabKind.Toolbox, "screenshot", "tb-screenshot"),
        (MainTabKind.Toolbox, "crash", "tb-crash"),
        (MainTabKind.Toolbox, "datapack", "tb-datapack"),
        (MainTabKind.Toolbox, "saves", "tb-saves"),
        (MainTabKind.Toolbox, "skin", "tb-skin"),
        (MainTabKind.Toolbox, "network", "tb-network"),
        (MainTabKind.Toolbox, "nbt", "tb-nbt"),
        (MainTabKind.Toolbox, "shortcut", "tb-shortcut"),
        (MainTabKind.Toolbox, "afk", "tb-afk"),
        (MainTabKind.Toolbox, "aichat", "tb-aichat"),
        (MainTabKind.Toolbox, "perf", "tb-perf"),
        (MainTabKind.Toolbox, "modpackio", "tb-modpackio"),
        (MainTabKind.Toolbox, "music", "tb-music"),
        (MainTabKind.Toolbox, "moddev", "tb-moddev"),
        (MainTabKind.Toolbox, "packmaker", "tb-packmaker"),
        (MainTabKind.Toolbox, "command", "tb-command"),

        (MainTabKind.Settings, "appearance", "st-appearance"),
        (MainTabKind.Settings, "account", "st-account"),
        (MainTabKind.Settings, "general", "st-general"),
        (MainTabKind.Settings, "launch", "st-launch"),
        (MainTabKind.Settings, "download", "st-download"),
        (MainTabKind.Settings, "recommend", "st-recommend"),
        (MainTabKind.Settings, "ai", "st-ai"),
        (MainTabKind.Settings, "about", "st-about"),
    };

    [STAThread]
    private static int Main(string[] args)
    {
        var outDir = Path.GetFullPath(args.Length > 0 ? args[0] : "/workspace/shots");
        Directory.CreateDirectory(outDir);

        var app = AppBuilder.Configure<MCLCS.Linux.App.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        MainViewModel.Instance = new MainViewModel();
        EnsureShotVersion();   // 为版本设置对话框准备一个带成就的演示版本
        var failures = 0;

        // ---- 真实 MainWindow：完整壳层（侧栏 + 主标签 + 状态栏），暗 / 亮双轮全量 ----
        var mw = new MainWindow { Width = W, Height = H };
        mw.Show();
        Dispatcher.UIThread.RunJobs();

        foreach (var theme in new[] { ThemeType.Dark, ThemeType.Light })
        {
            // 切换主题（触发 App.ApplyTheme 写入亮/暗调色板 + Fluent 变体）
            ThemeManager.Current = theme;
            Dispatcher.UIThread.RunJobs();
            var suffix = theme == ThemeType.Light ? "-light" : "";

            foreach (var (kind, sid, name) in Nav)
            {
                try
                {
                    mw.NavigateTo(kind, sid);
                    Dispatcher.UIThread.RunJobs();

                    // 下载子页：等真实网络搜索 / 版本清单加载完成（cards 有内容再截）。
                    // 网络实测：version_manifest ≈ 6s、Modrinth < 1s。
                    // 注意：不能 await Task.Delay（[STAThread] 主线程 async Main 无消息泵会死锁），
                    // 用 Thread.Sleep + RunJobs 交替推进 UI 队列，让网络回调 continuation 落地。
                    if (kind == MainTabKind.Download)
                        WaitForNetwork(sid == "minecraft" ? 9000 : 6000);
                    Dispatcher.UIThread.RunJobs();

                    // 注入演示数据：让硬编码背景卡片在亮模式下真实渲染，验证双模式字体亮度修复
                    InjectDemoData(mw, sid);

                    // 皮肤页：注入真实皮肤到皮肤编辑器（同时切到 3D 预览，关自动旋转）
                    if (sid == "skin")
                    {
                        var editorView = mw.GetVisualDescendants().OfType<SkinEditorView>().FirstOrDefault();
                        if (editorView is { DataContext: SkinEditorViewModel evm })
                        {
                            evm.LoadFromSkia(LoadRealSkinSkia());  // 写入 _pixels + FlushFull → FullBitmap 通知
                            // 切到 3D 预览：公共方法同时设 Border.IsVisible + VM.IsEditing2D + ToggleButton.IsChecked
                            // （单设任一项 headless 下 binding/trigger 偶发不生效，三处同步最稳）
                            editorView.Show3D();
                            // 验证
                            var ed2 = mw.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "Editor2D");
                            var pv3 = mw.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "Preview3D");
                            Console.WriteLine($"  [dbg-skin] Show3D后 IsEditing2D={evm.IsEditing2D} Editor2D.IsVisible={ed2?.IsVisible} Preview3D.IsVisible={pv3?.IsVisible} evm.HasSkin={evm.HasSkin} FullBitmap!=null={evm.FullBitmap != null}");
                            Console.WriteLine($"  [dbg-skin] Mode2D.IsChecked={editorView.FindControl<ToggleButton>("Mode2D")?.IsChecked} Mode3D.IsChecked={editorView.FindControl<ToggleButton>("Mode3D")?.IsChecked}");
                        }
                        foreach (var c in mw.GetVisualDescendants().OfType<SkinPreview3D>())
                            c.AutoRotate = false;                // headless DispatcherTimer 会卡 RunJobs
                        Dispatcher.UIThread.RunJobs();
                        Thread.Sleep(400);                         // 给 SkinPreview3D 几帧渲染时间
                        Dispatcher.UIThread.RunJobs();
                        // 记录 SkinPreview3D 在窗口中的矩形（headless 下 Image 不绘制位图，后期用 skin3d 合成进该区域）
                        var pv = mw.GetVisualDescendants().OfType<SkinPreview3D>().FirstOrDefault();
                        if (pv is not null)
                        {
                            var pt = pv.TranslatePoint(new Point(0, 0), mw);
                            _previewSize = new Avalonia.Size(pv.Bounds.Width, pv.Bounds.Height);
                            var rect = $"{(pt?.X ?? 0):F0} {(pt?.Y ?? 0):F0} {pv.Bounds.Width:F0} {pv.Bounds.Height:F0}";
                            Console.WriteLine($"  [dbg-skin] Preview3D 窗口矩形=({rect})");
                            try { File.WriteAllText(Path.Combine(outDir, "skin_preview_rect.txt"), rect); } catch { }
                        }
                    }

                    var path = Path.Combine(outDir, $"{name}{suffix}.png");
                    Capture(mw, path);
                    Console.WriteLine($"[ok]   {theme,-5} {name,-16} -> {path}  " +
                        (kind == MainTabKind.Download ? $"cards={DownloadPageViewModel.Instance.Cards.Count}" : ""));

                    // 游戏页捕获后，额外抓一张「版本设置」对话框（含版本隔离开关 + 成就）
                    if (name == "home")
                        CaptureVersionSettings(mw, outDir, suffix);
                }
                catch (Exception ex)
                {
                    failures++;
                    Console.Error.WriteLine($"[fail] {theme} {name}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        mw.Close();

        // ---- 皮肤 3D 渲染管线直出（真实皮肤，确定性验证）----
        try
        {
            using var skin = LoadRealSkinSkia();
            using var frame = Skin3DRenderer.Render(skin, slim: false, yawDeg: 35, pitchDeg: -8, camZ: 58,
                (int)_previewSize.Width, (int)_previewSize.Height);
            var p3d = Path.Combine(outDir, "skin3d.png");
            using (var fs = File.Create(p3d))
                frame.Encode(SKEncodedImageFormat.Png, 100).SaveTo(fs);
            Console.WriteLine($"[ok]   skin3d         -> {p3d}");
        }
        catch (Exception ex)
        {
            failures++;
            Console.Error.WriteLine($"[fail] skin3d: {ex.GetType().Name}: {ex.Message}");
        }

        // 后处理：headless 下 Image 控件不绘制位图，把 skin3d 直出合成进皮肤页 3D 区
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("python3", $"tools/MCLCS.Linux.Shot/postprocess_skin.py \"{outDir}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is not null)
            {
                proc.WaitForExit();
                Console.Write(proc.StandardOutput.ReadToEnd());
                var err = proc.StandardError.ReadToEnd();
                if (!string.IsNullOrEmpty(err)) Console.Error.WriteLine(err);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[warn] skin postprocess failed: {ex.Message}");
        }

        // ---- UI 组件：确认对话框 / Toast ----
        try
        {
            var dlg = new ConfirmDialog("确认恢复",
                "将备份「Test World（2026-08-17 15:00）」恢复到：\n/root/.minecraft/saves/Test World\n\n恢复前会自动备份当前状态。\n\n确定继续？",
                "确定", danger: true);
            var p1 = Path.Combine(outDir, "ui-confirm.png");
            RenderWindow(dlg, p1);
            Console.WriteLine($"[ok]   ui-confirm     -> {p1}");
        }
        catch (Exception ex)
        {
            failures++;
            Console.Error.WriteLine($"[fail] ui-confirm: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            var toast = new ToastWindow("备份完成", "存档 · Test World → 3.4 MB", ToastKind.Success);
            var p2 = Path.Combine(outDir, "ui-toast.png");
            RenderWindow(toast, p2);
            Console.WriteLine($"[ok]   ui-toast       -> {p2}");
        }
        catch (Exception ex)
        {
            failures++;
            Console.Error.WriteLine($"[fail] ui-toast: {ex.GetType().Name}: {ex.Message}");
        }

        // 文件变更检测详情窗口（自动检测 Toast「查看详情」打开的窗口）
        try
        {
            var fwWin = new FileWatchWindow();
            fwWin.Show();
            Dispatcher.UIThread.RunJobs();

            FileWatchViewModel? fwvm = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var sawBusy = false;
            while (sw.ElapsedMilliseconds < 5000)
            {
                fwvm = fwWin.GetVisualDescendants().OfType<FileWatchView>().FirstOrDefault()?.DataContext as FileWatchViewModel;
                if (fwvm is null) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(50); continue; }
                if (fwvm.IsBusy) sawBusy = true;
                else if (sawBusy) break;   // 等 RefreshAsync 完成一轮「忙→闲」再注入，避免被覆盖
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(50);
            }
            fwvm ??= fwWin.GetVisualDescendants().OfType<FileWatchView>().FirstOrDefault()?.DataContext as FileWatchViewModel;
            if (fwvm is not null)
            {
                fwvm.Changes = new ObservableCollection<FileChange>(new[]
                {
                    new FileChange(FileChangeKind.Modified, "mods/optifine_1.20.1.jar", "哈希变化 a1b2→c3d4"),
                    new FileChange(FileChangeKind.Added, "resourcepacks/faithful-64x.zip"),
                    new FileChange(FileChangeKind.Removed, "config/old-mod.toml"),
                });
                fwvm.Summary = "新增 1 个，删除 1 个，修改 1 个";
                fwvm.Status = "下列变更尚未「标记为已知」，下次启动 / 回到启动器会再次提醒";
            }
            Dispatcher.UIThread.RunJobs();
            var p3 = Path.Combine(outDir, "ui-filewatch.png");
            RenderWindow(fwWin, p3);
            Console.WriteLine($"[ok]   ui-filewatch   -> {p3}");
        }
        catch (Exception ex)
        {
            failures++;
            Console.Error.WriteLine($"[fail] ui-filewatch: {ex.GetType().Name}: {ex.Message}");
        }

        Console.WriteLine(failures == 0 ? "ALL OK" : $"{failures} FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static SkinViewModel? FindSkinVm(Window mw)
    {
        foreach (var c in mw.GetVisualDescendants())
        {
            if (c is Control { DataContext: SkinViewModel svm }) return svm;
        }
        return null;
    }

    /// <summary>为截图准备一个演示版本（含成就数据），使游戏页版本列表与版本设置对话框有内容可渲染。</summary>
    private static void EnsureShotVersion()
    {
        try
        {
            var root = GameConstants.DefaultGameRoot;
            var vdir = Path.Combine(root, "versions", "ShotDemo");
            Directory.CreateDirectory(vdir);
            File.WriteAllText(Path.Combine(vdir, "ShotDemo.json"), "{\"type\":\"release\"}");
            var adv = Path.Combine(root, "saves", "ShotWorld", "advancements");
            Directory.CreateDirectory(adv);
            File.WriteAllText(Path.Combine(adv, "root.json"), "{\"done\":true,\"display\":{\"frame\":\"challenge\"}}");
            File.WriteAllText(Path.Combine(adv, "story.json"), "{\"done\":false}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[warn] EnsureShotVersion failed: {ex.Message}");
        }
    }

    /// <summary>打开「版本设置」对话框并截图（不等待用户关闭，截完即 Complete 隐藏）。</summary>
    private static void CaptureVersionSettings(MainWindow mw, string outDir, string suffix)
    {
        var hvm = mw.GetVisualDescendants().OfType<HomeView>().FirstOrDefault()?.DataContext as HomeViewModel;
        var sel = hvm?.SelectedVersion;
        if (sel is null) return;

        var vm = new VersionSettingsViewModel(sel.Id, sel.Type, GameConstants.DefaultGameRoot);
        // 演示：注入账号以展示「账户绑定」选择器，并预选第一个 + 打开版本锁定
        if (vm.Accounts.Count == 0)
        {
            vm.Accounts.Add(new AccountEntry { Id = "demo-steve", DisplayName = "Steve", AuthType = "offline" });
            vm.Accounts.Add(new AccountEntry { Id = "demo-alex", DisplayName = "Alex（微软）", AuthType = "microsoft" });
        }
        vm.BoundAccount = vm.Accounts[0];
        vm.Locked = true;
        var view = new VersionSettingsView { DataContext = vm };
        _ = DialogService.Instance.ShowAsync(new DialogOptions
        {
            Title = $"版本设置 · {sel.Id}",
            Content = view,
            Buttons = new[] { new DialogButton("关闭", isCancel: true) },
            Width = 560,
        });

        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(300);
        Dispatcher.UIThread.RunJobs();
        // 滚动到「账户绑定 / 版本锁定」区域以完整展示新增功能
        if (view.Content is Avalonia.Controls.ScrollViewer sv) sv.ScrollToEnd();
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(300);

        var path = Path.Combine(outDir, $"version-settings{suffix}.png");
        Capture(mw, path);
        Console.WriteLine($"[ok]   version-settings{suffix} -> {path}");

        DialogService.Instance.Complete(null);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>阻塞等待网络结果落地：Thread.Sleep 让异步 I/O 在 ThreadPool 完成，
    /// 周期性 RunJobs 执行其 continuation（回填集合 / 更新 IsBusy）。</summary>
    private static void WaitForNetwork(int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Thread.Sleep(300);
            Dispatcher.UIThread.RunJobs();
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static void Capture(Window window, string path)
    {
        // headless false 模式下，局部 IsVisible 切换不会自动触发合成层重绘（CaptureRenderedFrame 可能取到
        // NavigateTo 时的旧帧）。Hide + Show 强制整窗 CompositionTarget 重建，使 3D 预览（Preview3D 可见 /
        // Editor2D 隐藏）按当前逻辑状态重新合成。
        window.InvalidateVisual();
        Dispatcher.UIThread.RunJobs();
        window.Hide();
        window.Show();
        Dispatcher.UIThread.RunJobs();
        using var bmp = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("CaptureRenderedFrame 返回 null");
        using (var fs = File.Create(path))
            bmp.Save(fs);
    }

    /// <summary>
    /// 给若干 Toolbox / Settings 子页注入演示数据，让原本无数据的 ItemsControl 卡片真正渲染，
    /// 以便截图中验证「硬编码深色卡片背景 → 动态 CardBackground」在亮模式下的修复效果。
    /// </summary>
    private static void InjectDemoData(MainWindow mw, string sid)
    {
        switch (sid)
        {
            case "network":
                if (mw.GetVisualDescendants().OfType<NetworkView>().FirstOrDefault()?.DataContext is NetworkViewModel nv)
                {
                    nv.Results.Add(new DiagnosticResult { Name = "BMCLAPI 镜像", Url = "https://bmclapi.cn", Reachable = true, LatencyMs = 23 });
                    nv.Results.Add(new DiagnosticResult { Name = "Mojang 官方", Url = "https://piston-meta.mojang.com", Reachable = false, Error = "连接超时" });
                }
                break;
            case "download": // DownloadSettingsView
                if (mw.GetVisualDescendants().OfType<DownloadSettingsView>().FirstOrDefault()?.DataContext is DownloadSettingsViewModel ds)
                {
                    ds.MirrorUrls.Add("https://bmclapi.cn/version/{id}/version.json");
                    ds.MirrorUrls.Add("https://mirror.nju.edu.cn/minecraft/");
                }
                break;
            case "recommend":
                if (mw.GetVisualDescendants().OfType<RecommendSettingsView>().FirstOrDefault()?.DataContext is RecommendSettingsViewModel rs)
                {
                    rs.Items.Add("建议分配 4 GB 内存（当前 2 GB）—— 大型整合包易 OOM");
                    rs.Items.Add("启用异步资产加载以缩短启动时间约 18%");
                }
                break;
            case "saves":
                if (mw.GetVisualDescendants().OfType<SavesView>().FirstOrDefault()?.DataContext is SavesViewModel sv)
                {
                    sv.CompatReports.Add(new SaveCompatibilityReport
                    {
                        SaveName = "测试世界 A",
                        SaveGameVersion = "1.20.4",
                        Severity = SaveCompatibilitySeverity.SlightlyNewer,
                        Message = "存档版本略新于当前游戏版本，可直接降级且风险较低。",
                    });
                }
                break;
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>渲染独立窗口（对话框 / Toast 等），先强制布局收敛再截图。</summary>
    private static void RenderWindow(Window window, string path)
    {
        window.Show();
        window.Measure(new Size(window.Width, double.PositiveInfinity));
        window.Arrange(new Rect(0, 0, window.DesiredSize.Width, window.DesiredSize.Height));
        Dispatcher.UIThread.RunJobs();
        window.InvalidateVisual();
        Dispatcher.UIThread.RunJobs();

        using var bmp = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("CaptureRenderedFrame 返回 null");
        using (var fs = File.Create(path))
            bmp.Save(fs);
        window.Close();
    }

    /// <summary>生成 64×64 测试皮肤（各部位不同颜色，便于目视验证 UV 映射）。</summary>
    private static SKBitmap CreateTestSkinSkia()
    {
        var sk = new SKBitmap(64, 64, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(sk);
        canvas.Clear(new SKColor(0x2E, 0x34, 0x40)); // 底色（默认像素）

        // 头 x0-32 y0-16：肤色 + 眼睛
        using var head = new SKPaint { Color = new SKColor(0xF5, 0xC9, 0xA8) };
        canvas.DrawRect(0, 0, 32, 16, head);
        using var eye = new SKPaint { Color = new SKColor(0x1F, 0x29, 0x33) };
        canvas.DrawRect(8, 8, 4, 4, eye);
        canvas.DrawRect(20, 8, 4, 4, eye);

        // 帽子 x32-64 y0-16
        using var hat = new SKPaint { Color = new SKColor(0xC0, 0x39, 0x2B) };
        canvas.DrawRect(32, 0, 32, 16, hat);

        // 躯干 x16-40 y16-32
        using var body = new SKPaint { Color = new SKColor(0x2E, 0x86, 0xC1) };
        canvas.DrawRect(16, 16, 24, 16, body);

        // 右臂 x40-56 y16-32 / 右腿 x0-16 y16-32
        using var limb = new SKPaint { Color = new SKColor(0xE6, 0x7E, 0x22) };
        canvas.DrawRect(40, 16, 16, 16, limb);
        canvas.DrawRect(0, 16, 16, 16, limb);

        // 外套 x16-40 y32-48
        using var jacket = new SKPaint { Color = new SKColor(0x1A, 0x5F, 0x8C) };
        canvas.DrawRect(16, 32, 24, 16, jacket);

        // 右裤 x0-16 y32-48 / 右袖 x40-56 y32-48
        using var pants = new SKPaint { Color = new SKColor(0x8E, 0x44, 0xAD) };
        canvas.DrawRect(0, 32, 16, 16, pants);
        canvas.DrawRect(40, 32, 16, 16, pants);

        // 左腿 x16-32 y48-64 / 左臂 x32-48 y48-64 / 左裤 x0-16 y48-64 / 左袖 x48-64 y48-64
        using var left = new SKPaint { Color = new SKColor(0x16, 0xA0, 0x85) };
        canvas.DrawRect(16, 48, 16, 16, left);
        canvas.DrawRect(32, 48, 16, 16, left);
        using var left2 = new SKPaint { Color = new SKColor(0x0E, 0x6B, 0x58) };
        canvas.DrawRect(0, 48, 16, 16, left2);
        canvas.DrawRect(48, 48, 16, 16, left2);

        return sk;
    }

    private static Bitmap CreateTestSkin()
    {
        using var sk = CreateTestSkinSkia();
        using var png = sk.Encode(SKEncodedImageFormat.Png, 100);
        using var ms = new MemoryStream();
        png.SaveTo(ms);
        ms.Position = 0;
        return new Bitmap(ms);
    }

    /// <summary>真实皮肤路径（@daidr/minecraft-skin-renderer 库自带示例皮肤，用户提供）。</summary>
    private const string RealSkinPath = "/workspace/real-skin.png";

    /// <summary>加载真实皮肤为 SKBitmap（供 3D 渲染管线直出）。</summary>
    private static SKBitmap LoadRealSkinSkia()
    {
        if (!File.Exists(RealSkinPath))
            throw new FileNotFoundException($"真实皮肤不存在：{RealSkinPath}");
        return SKBitmap.Decode(RealSkinPath);
    }

    /// <summary>加载真实皮肤为 Avalonia Bitmap（供启动器皮肤页注入）。</summary>
    private static Bitmap LoadRealSkin()
    {
        using var sk = LoadRealSkinSkia();
        using var png = sk.Encode(SKEncodedImageFormat.Png, 100);
        using var ms = new MemoryStream();
        png.SaveTo(ms);
        ms.Position = 0;
        return new Bitmap(ms);
    }
}
