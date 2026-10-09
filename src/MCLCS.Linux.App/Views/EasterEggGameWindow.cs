using System;
using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using MCLCS.Core.Badges;
using MCLCS.Core.Utils;

namespace MCLCS.Linux.App.Views;

/// <summary>
/// 清单 #34：愚人节彩蛋小游戏 —— 「抓假方块」（对齐 WPF EasterEggGameWindow）。
/// 5×5 格中会随机冒出一个「假方块」（?），在它溜走前点中即得分，点空扣分。
/// 全年可用（隐藏入口触发），愚人节期间在节日中心露出入口。完成后解锁隐藏勋章。
/// </summary>
public class EasterEggGameWindow : Window
{
    private const int GridSize = 5;
    private const int RoundSeconds = 45;

    private readonly Border[,] _cells = new Border[GridSize, GridSize];
    private readonly DispatcherTimer _spawnTimer;
    private readonly DispatcherTimer _countTimer;
    private readonly TextBlock _scoreText;
    private readonly TextBlock _bestText;
    private readonly TextBlock _timeText;
    private readonly Button _startBtn;
    private readonly TextBlock _hintText;

    private int _score;
    private int _best;
    private int _activeRow = -1;
    private int _activeCol = -1;
    private int _remain = RoundSeconds;
    private bool _running;

    public EasterEggGameWindow()
    {
        Title = "？？？";
        Width = 460;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        CanResize = false;
        Background = FindBrush("WindowBackground") ?? new SolidColorBrush(Color.FromRgb(0x20, 0x24, 0x2C));
        Foreground = FindBrush("PrimaryForeground") ?? new SolidColorBrush(Colors.White);

        var root = new StackPanel { Margin = new Thickness(16) };

        root.Children.Add(new TextBlock
        {
            Text = "抓 假 方 块",
            FontSize = 20,
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
        });
        _hintText = new TextBlock
        {
            Text = "假方块会随机冒出来 —— 在它溜走前点中它。点空要扣分。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 10),
            Foreground = FindBrush("SecondaryForeground") ?? Brushes.LightGray
        };
        root.Children.Add(_hintText);

        var stat = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        stat.ColumnDefinitions.Add(new ColumnDefinition());
        stat.ColumnDefinitions.Add(new ColumnDefinition());
        stat.ColumnDefinitions.Add(new ColumnDefinition());
        _scoreText = Stat("得分 0");
        _bestText = Stat("最佳 0");
        _timeText = Stat($"剩余 {RoundSeconds}s");
        Grid.SetColumn(_scoreText, 0);
        Grid.SetColumn(_bestText, 1);
        Grid.SetColumn(_timeText, 2);
        stat.Children.Add(_scoreText);
        stat.Children.Add(_bestText);
        stat.Children.Add(_timeText);
        root.Children.Add(stat);

        var board = new Grid { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
        for (var i = 0; i < GridSize; i++)
        {
            board.RowDefinitions.Add(new RowDefinition(new GridLength(64)));
            board.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(64)));
        }
        for (var r = 0; r < GridSize; r++)
        {
            for (var c = 0; c < GridSize; c++)
            {
                var cell = new Border
                {
                    Width = 58,
                    Height = 58,
                    Margin = new Thickness(3),
                    CornerRadius = new CornerRadius(6),
                    Background = new SolidColorBrush(Color.FromArgb(0x33, 0xAA, 0xAA, 0xAA)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
                    BorderThickness = new Thickness(1),
                    Cursor = new Cursor(StandardCursorType.Hand)
                };
                var label = new TextBlock
                {
                    FontSize = 24,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                };
                cell.Child = label;
                var row = r;
                var col = c;
                // Avalonia 没有 MouseLeftButtonUp，用 PointerReleased（等价的左键抬起）
                cell.PointerReleased += (_, _) => Hit(row, col);
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                _cells[r, c] = cell;
                board.Children.Add(cell);
            }
        }
        root.Children.Add(board);

        _startBtn = new Button
        {
            Content = "开始（45 秒）",
            Margin = new Thickness(0, 14, 0, 0),
            Padding = new Thickness(16, 6, 16, 6),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
        };
        _startBtn.Click += (_, _) => { if (_running) Stop(); else Start(); };
        root.Children.Add(_startBtn);

        Content = root;

        _spawnTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(950) };
        _spawnTimer.Tick += (_, _) => Spawn();
        _countTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countTimer.Tick += OnCountTick;

        LoadBest();
        _bestText.Text = $"最佳 {_best}";
        // 关窗必须停表：DispatcherTimer 的 Tick 委托反向引用窗口，不停则永不回收
        Closed += (_, _) => { _spawnTimer.Stop(); _countTimer.Stop(); };
    }

    private static TextBlock Stat(string text) => new()
    {
        Text = text,
        FontSize = 13,
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        Foreground = FindBrush("PrimaryForeground") ?? Brushes.White
    };

    private static IBrush? FindBrush(string key)
    {
        if (Application.Current is { } app && app.TryFindResource(key, out var res))
            return res as IBrush;
        return null;
    }

    private void Start()
    {
        _score = 0;
        _remain = RoundSeconds;
        _running = true;
        _startBtn.Content = "停止";
        _scoreText.Text = "得分 0";
        _timeText.Text = $"剩余 {_remain}s";
        _spawnTimer.Start();
        _countTimer.Start();
        Spawn();
    }

    private void Stop()
    {
        _running = false;
        _spawnTimer.Stop();
        _countTimer.Stop();
        _startBtn.Content = "再来一局";
        ClearActive();
        if (_score > _best)
        {
            _best = _score;
            _bestText.Text = $"最佳 {_best}";
            SaveBest();
        }
        _hintText.Text = _score >= 20
            ? "手速惊人……你是不是提前知道方块会从哪儿冒出来？"
            : "再来？假方块可比末影人好抓。";
        UnlockBadge();
    }

    private void OnCountTick(object? sender, EventArgs e)
    {
        _remain--;
        _timeText.Text = $"剩余 {Math.Max(0, _remain)}s";
        if (_remain <= 0) Stop();
    }

    private void Spawn()
    {
        ClearActive();
        var r = Random.Shared.Next(GridSize);
        var c = Random.Shared.Next(GridSize);
        _activeRow = r;
        _activeCol = c;
        var cell = _cells[r, c];
        cell.Background = new SolidColorBrush(Color.FromArgb(0xDD, 0xE0, 0x5A, 0x2A));
        if (cell.Child is TextBlock t) t.Text = "?";
    }

    private void ClearActive()
    {
        if (_activeRow < 0 || _activeCol < 0) return;
        var cell = _cells[_activeRow, _activeCol];
        cell.Background = new SolidColorBrush(Color.FromArgb(0x33, 0xAA, 0xAA, 0xAA));
        if (cell.Child is TextBlock t) t.Text = "";
        _activeRow = -1;
        _activeCol = -1;
    }

    private void Hit(int row, int col)
    {
        if (!_running) return;
        if (row == _activeRow && col == _activeCol)
        {
            _score += 1;
            _scoreText.Text = $"得分 {_score}";
            ClearActive();
            Spawn();
        }
        else if (_score > 0)
        {
            _score -= 1;
            _scoreText.Text = $"得分 {_score}";
        }
    }

    private static void UnlockBadge()
    {
        try
        {
            BadgeService.Unlock(BadgeIds.HiddenEasterEgg);
            BadgeService.Unlock(BadgeIds.SeasonalFirstEvent);
        }
        catch { /* 勋章非关键 */ }
    }

    // ===== 最佳成绩持久化（独立小文件，避免污染 profile）=====

    private static string BestPath =>
        Path.Combine(GameConstants.DefaultGameRoot, "mclcs_easteregg.json");

    private void LoadBest()
    {
        try
        {
            if (!File.Exists(BestPath)) return;
            var json = File.ReadAllText(BestPath);
            var doc = JsonSerializer.Deserialize<EggSave>(json);
            if (doc is not null) _best = doc.Best;
        }
        catch { /* ignore */ }
    }

    private void SaveBest()
    {
        try
        {
            Directory.CreateDirectory(GameConstants.DefaultGameRoot);
            File.WriteAllText(BestPath, JsonSerializer.Serialize(new EggSave { Best = _best }));
        }
        catch { /* ignore */ }
    }

    private sealed class EggSave
    {
        public int Best { get; set; }
    }
}
