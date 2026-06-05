// Copyright (c) 2026 猫猫越 <liu2128783468@outlook.com>
// Licensed under MIT — 修改请保留本声明
// Legacy 浮窗 — 双窗口架构：弹幕窗物理隔离 + 控制窗独立交互
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Input;
using System.Runtime.InteropServices;

// ══════ 弹幕渲染窗（全屏、永远透明、零鼠标干扰）══════
public class DanmakuWindow : Window
{
    protected ClientWebSocket _ws;
    protected Canvas _canvas;
    protected List<double> _lanes = new List<double>();
    protected List<DateTime> _laneFreeTime = new List<DateTime>();
    protected int _laneIndex;
    protected double _speed = 10, _fontSize = 28;
    protected int _laneCount = 15, _regionMode;
    protected const int MaxDanmaku = 50;
    protected string _customColor;
    public bool Paused = false;
    public bool PlusMode = false;
    public bool AntiOverlap = false;
    protected List<DanmakuItem> _activeItems = new List<DanmakuItem>();
    protected Dictionary<string, SolidColorBrush> _brushCache = new Dictionary<string, SolidColorBrush>();
    protected List<string> _pendingJson = new List<string>();
    protected class DanmakuItem
    {
        public UIElement Element;
        public double StartX, EndX, MeasuredWidth;
        public DateTime StartTime;
        public double Duration;
    }

    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    const int GWL_EXSTYLE = -20;
    const int WS_EX_TRANSPARENT = 0x20, WS_EX_NOACTIVATE = 0x08000000, WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80;
    IntPtr _hwnd;

    public DanmakuWindow()
    {
        AllowsTransparency = true;
        WindowStyle = WindowStyle.None;
        Topmost = true;
        ShowInTaskbar = false;
        Background = Brushes.Transparent;
        WindowState = WindowState.Maximized;
        Focusable = false;

        _canvas = new Canvas();
        Content = _canvas;
        RebuildLanes();

        Loaded += (s, e) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
            SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_LAYERED | WS_EX_TOOLWINDOW);
            CompositionTarget.Rendering += OnRender_Rendering;
        };
        Closed += (s, e) => CompositionTarget.Rendering -= OnRender_Rendering;
    }

    public void ApplySettings(double speed, double fontSize, int lanes, int regionMode, string customColor)
    {
        _speed = speed; _fontSize = fontSize; _laneCount = lanes; _regionMode = regionMode; _customColor = customColor;
        RebuildLanes();
    }

    protected void RebuildLanes()
    {
        _lanes.Clear(); _laneFreeTime = new List<DateTime>(); _laneIndex = 0;
        while (_canvas.Children.Count > 1) _canvas.Children.RemoveAt(1);
        if (_laneCount < 1) _laneCount = 1;
        double topPct = 5, botPct = 95;
        if (_regionMode == 1) { topPct = 2; botPct = 20; }
        else if (_regionMode == 2) { topPct = 80; botPct = 98; }
        for (int i = 0; i < _laneCount; i++) { _lanes.Add(topPct + i * ((botPct - topPct) / _laneCount)); _laneFreeTime.Add(DateTime.MinValue); }
    }

    protected int FindFreeLane()
    {
        double minGap = _speed * 0.4; var now = DateTime.Now;
        for (int a = 0; a < _laneCount; a++)
        { if (now >= _laneFreeTime[_laneIndex]) { int idx = _laneIndex; _laneIndex = (_laneIndex + 1) % _laneCount; _laneFreeTime[idx] = now.AddSeconds(minGap); return idx; } _laneIndex = (_laneIndex + 1) % _laneCount; }
        int best = 0;
        for (int i = 1; i < _laneCount; i++) if (_laneFreeTime[i] < _laneFreeTime[best]) best = i;
        _laneFreeTime[best] = now.AddSeconds(minGap);
        return best;
    }

    public async Task Connect(string url)
    {
        while (true)
        {
            try { _ws = new ClientWebSocket(); await _ws.ConnectAsync(new Uri(url), CancellationToken.None); await ReceiveLoop(); }
            catch { }
            await Task.Delay(3000);
        }
    }

    protected async Task ReceiveLoop()
    {
        var buf = new byte[8192]; var leftover = "";
        while (_ws != null && _ws.State == WebSocketState.Open)
        {
            try
            {
                var r = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                var data = leftover + Encoding.UTF8.GetString(buf, 0, r.Count);
                var lines = data.Split('\n');
                leftover = data.EndsWith("\n") ? "" : lines[lines.Length - 1];
                for (int i = 0; i < lines.Length - 1; i++)
                {
                    var json = lines[i].Trim();
                    if (json.Length == 0 || json.Contains("\"cmd\"") || Paused) continue;
                    lock (_pendingJson) { _pendingJson.Add(json); }
                }
            }
            catch { break; }
        }
    }

    protected void Render(string json)
    {
        try
        {
            string text = JsonVal(json, "text") ?? "";
            string colorHex = string.IsNullOrEmpty(_customColor) ? (JsonVal(json, "colorHex") ?? "#ffffff") : _customColor;
            string username = JsonVal(json, "username") ?? "";
            double size = _fontSize > 0 ? _fontSize : 12;
            bool isSC = JsonBool(json, "isSC"), isGift = JsonBool(json, "isGift");

            string display;
            if (isSC) display = "SC " + username + ": " + text;
            else if (isGift) display = username + " " + text;
            else if (!string.IsNullOrEmpty(username) && username != "unknown") display = username + ": " + text;
            else display = text;

            SolidColorBrush brush;
            if (!_brushCache.TryGetValue(colorHex, out brush))
            {
                Color color;
                try { color = (Color)ColorConverter.ConvertFromString(colorHex); } catch { color = Colors.White; }
                brush = new SolidColorBrush(color);
                if (colorHex == "#ffffff") brush.Freeze(); // 常用色冻结提升性能
                _brushCache[colorHex] = brush;
                            }

            var tb = new TextBlock { Text = display, FontSize = size, Foreground = brush, FontWeight = FontWeights.Bold, IsHitTestVisible = false };
            var laneIdx = FindFreeLane();
            var lane = _lanes[laneIdx];
            if (AntiOverlap) { lane += Math.Sin((double)laneIdx * 2.3 + Environment.TickCount * 0.001) * 1.5; }
            Canvas.SetTop(tb, lane * SystemParameters.PrimaryScreenHeight / 100);
            _canvas.Children.Add(tb);
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double textW = tb.DesiredSize.Width, screenW = SystemParameters.PrimaryScreenWidth;
            Canvas.SetLeft(tb, screenW + 10);
            _activeItems.Add(new DanmakuItem { Element = tb, StartX = screenW + 10, EndX = -textW - 10, MeasuredWidth = textW, StartTime = DateTime.Now, Duration = _speed });
        }
        catch { }
    }

    protected void OnRender_Rendering(object sender, EventArgs e)
    {
        // 批量消耗待处理弹幕
        List<string> batch = null;
        lock (_pendingJson) { if (_pendingJson.Count > 0) { batch = _pendingJson; _pendingJson = new List<string>(); } }
        if (batch != null) { foreach (var json in batch) Render(json); }

        // 更新弹幕位置
        var now = DateTime.Now; var toRemove = new List<DanmakuItem>();
        foreach (var item in _activeItems)
        {
            double progress = Math.Min((now - item.StartTime).TotalSeconds / item.Duration, 1.0);
            double x = item.StartX + (item.EndX - item.StartX) * progress;
            Canvas.SetLeft(item.Element, x);
            if (item.MeasuredWidth > 0 && x + item.MeasuredWidth <= 0) toRemove.Add(item);
        }
        foreach (var item in toRemove) { _activeItems.Remove(item); try { _canvas.Children.Remove(item.Element as UIElement); } catch { } }
    }

    static string JsonVal(string json, string key)
    {
        var m = Regex.Match(json, "\"" + key + "\":\\s*(\"[^\"]*\"|[^,}}]+)");
        if (!m.Success) return null;
        var v = m.Groups[1].Value.Trim();
        if (v.StartsWith("\"") && v.EndsWith("\"")) return v.Substring(1, v.Length - 2).Replace("\\\"", "\"");
        return v;
    }
    static bool JsonBool(string json, string key) { return JsonVal(json, key) == "true"; }

    public void SetPlusMode(bool on)
    {
        PlusMode = on;
    }

    public void ClearCache()
    {
        lock (_pendingJson) { _pendingJson.Clear(); }
        _brushCache.Clear();
        for (int i = _activeItems.Count - 1; i >= 0; i--)
        {
            double x = Canvas.GetLeft(_activeItems[i].Element);
            var fe = _activeItems[i].Element as FrameworkElement;
            if (fe != null && fe.ActualWidth > 0 && x + fe.ActualWidth <= 0)
            {
                try { _canvas.Children.Remove(_activeItems[i].Element); } catch { }
                _activeItems.RemoveAt(i);
            }
        }
    }

    public void SendMsg(string msg)
    {
        try { if (_ws != null && _ws.State == WebSocketState.Open) { var bytes = Encoding.UTF8.GetBytes(msg); _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None); } } catch { }
    }
}

// ══════ 控制面板窗（右下角、正常交互、隐身模式下不显示）══════
public class ControlWindow : Window
{
    private DanmakuWindow _danmaku;
    private bool _locked;
    private double _speed = 10, _fontSize = 28;
    private int _laneCount = 15, _regionMode;
    private string _customColor;
    private TextBox _roomBox;
    private Button _gearBtn;
    private StackPanel _settingsPanel;

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    const uint MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_ALT = 0x0001;
    const int WM_HOTKEY = 0x0312, GWL_EXSTYLE = -20;
    const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20;
    int _hotkeyId = 9001;
    IntPtr _hwnd;

    public ControlWindow(DanmakuWindow danmaku)
    {
        _danmaku = danmaku;
        AllowsTransparency = true;
        WindowStyle = WindowStyle.None;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Transparent;
        Width = 40; Height = 40;
        Left = SystemParameters.PrimaryScreenWidth - 70;
        Top = SystemParameters.PrimaryScreenHeight - 60;

        var root = new StackPanel();
        var panelArea = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(220, 20, 20, 20)),
            Child = root,
        };
        Content = panelArea;

        // 齿轮按钮
        var topRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

        _gearBtn = new Button
        {
            Content = "⚙",
            Width = 36, Height = 36, FontSize = 18,
            Background = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)),
            Foreground = new SolidColorBrush(Color.FromArgb(220, 200, 200, 200)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            Cursor = Cursors.Hand,
        };
        topRow.Children.Add(_gearBtn);
        root.Children.Add(topRow);

        // 设置面板
        _settingsPanel = new StackPanel { Visibility = Visibility.Collapsed };
        _gearBtn.Click += (s, e) =>
        {
            if (_locked) return;
            var open = _settingsPanel.Visibility != Visibility.Visible;
            _settingsPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            panelArea.Background = open ? new SolidColorBrush(Color.FromArgb(220, 20, 20, 20)) : null;
            Width = open ? 260 : 40; Height = open ? 500 : 40;
            Left = SystemParameters.PrimaryScreenWidth - (open ? 270 : 50);
            Top = open ? SystemParameters.PrimaryScreenHeight - 620 : SystemParameters.PrimaryScreenHeight - 60;
            // 面板打开时允许获取焦点（否则无法打字）
            var ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
            if (open) SetWindowLong(_hwnd, GWL_EXSTYLE, ex & ~WS_EX_NOACTIVATE);
            else SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE);
        };

        _settingsPanel.Children.Add(MakeLabel("Speed: 10s"));
        var spdSlider = MakeSlider(1, 20, 10);
        _settingsPanel.Children.Add(spdSlider);
        var spdLabel = _settingsPanel.Children[_settingsPanel.Children.Count - 2] as TextBlock;
        spdSlider.ValueChanged += (s, e) => { _speed = spdSlider.Value; spdLabel.Text = "Speed: " + (int)_speed + "s"; _danmaku.ApplySettings(_speed, _fontSize, _laneCount, _regionMode, _customColor); };

        _settingsPanel.Children.Add(MakeLabel("Font Size: 28px"));
        var fntSlider = MakeSlider(0, 48, 28);
        _settingsPanel.Children.Add(fntSlider);
        var fntLabel = _settingsPanel.Children[_settingsPanel.Children.Count - 2] as TextBlock;
        fntSlider.ValueChanged += (s, e) => { _fontSize = fntSlider.Value; fntLabel.Text = "Font Size: " + (int)_fontSize + "px"; _danmaku.ApplySettings(_speed, _fontSize, _laneCount, _regionMode, _customColor); };

        _settingsPanel.Children.Add(MakeLabel("Lanes: 15"));
        var laneSlider = MakeSlider(0, 30, 15);
        _settingsPanel.Children.Add(laneSlider);
        var lanesLbl = _settingsPanel.Children[_settingsPanel.Children.Count - 2] as TextBlock;
        laneSlider.ValueChanged += (s, e) => { _laneCount = (int)laneSlider.Value; lanesLbl.Text = "Lanes: " + _laneCount; _danmaku.ApplySettings(_speed, _fontSize, _laneCount, _regionMode, _customColor); };

        _settingsPanel.Children.Add(MakeLabel("Region:"));
        var regRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 2, 0, 0) };
        var btnTop = new Button { Content = "Top 1/5", Width = 70, Height = 24, FontSize = 10, Background = new SolidColorBrush(Color.FromArgb(60, 60, 60, 60)), Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(60, 100, 100, 100)), Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 5, 0) };
        var btnBot = new Button { Content = "Bottom 1/5", Width = 70, Height = 24, FontSize = 10, Background = new SolidColorBrush(Color.FromArgb(60, 60, 60, 60)), Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(60, 100, 100, 100)), Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 5, 0) };
        btnTop.Click += (s, e) => { _regionMode = _regionMode == 1 ? 0 : 1; btnTop.Background = _regionMode == 1 ? new SolidColorBrush(Color.FromArgb(120, 0, 180, 220)) : new SolidColorBrush(Color.FromArgb(60, 60, 60, 60)); _danmaku.ApplySettings(_speed, _fontSize, _laneCount, _regionMode, _customColor); };
        btnBot.Click += (s, e) => { _regionMode = _regionMode == 2 ? 0 : 2; btnBot.Background = _regionMode == 2 ? new SolidColorBrush(Color.FromArgb(120, 0, 180, 220)) : new SolidColorBrush(Color.FromArgb(60, 60, 60, 60)); _danmaku.ApplySettings(_speed, _fontSize, _laneCount, _regionMode, _customColor); };
        regRow.Children.Add(btnTop); regRow.Children.Add(btnBot);
        _settingsPanel.Children.Add(regRow);

        _settingsPanel.Children.Add(MakeLabel("Color:"));
        var colorHexes = new[] { "#ffffff", "#ff4444", "#44ff44", "#44aaff", "#ffff44", "#ff44ff", "#44ffff", "#ff8844", "#ff69b4", "#aaaaaa" };
        var colorRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 2, 0, 4) };
        Button prevColor = null;
        foreach (var hex in colorHexes)
        {
            var col = (Color)ColorConverter.ConvertFromString(hex);
            var cb = new Button { Width = 20, Height = 20, Margin = new Thickness(0, 0, 3, 0), Background = new SolidColorBrush(col), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(60, 120, 120, 120)), Cursor = Cursors.Hand, Tag = hex };
            cb.Click += (s, e) =>
            {
                var c = (Button)s;
                if (prevColor == c) { _customColor = null; prevColor = null; c.BorderThickness = new Thickness(1); }
                else { _customColor = (string)c.Tag; if (prevColor != null) prevColor.BorderThickness = new Thickness(1); c.BorderThickness = new Thickness(2); prevColor = c; }
                _danmaku.ApplySettings(_speed, _fontSize, _laneCount, _regionMode, _customColor);
            };
            colorRow.Children.Add(cb);
        }
        _settingsPanel.Children.Add(colorRow);

        _settingsPanel.Children.Add(MakeLabel("Room ID:"));
        var roomRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 2, 0, 0) };
        _roomBox = new TextBox { Width = 140, Height = 22, FontSize = 11, Background = new SolidColorBrush(Color.FromArgb(80, 40, 40, 40)), Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(60, 100, 100, 100)), VerticalContentAlignment = VerticalAlignment.Center };
        var applyBtn = new Button { Content = "Apply", Width = 50, Height = 22, FontSize = 10, Background = new SolidColorBrush(Color.FromArgb(60, 60, 60, 60)), Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(60, 100, 100, 100)), Cursor = Cursors.Hand, Margin = new Thickness(5, 0, 0, 0) };
        applyBtn.Click += (s, e) => { int id; if (int.TryParse(_roomBox.Text.Trim(), out id) && id > 0) _danmaku.SendMsg("{\"cmd\":\"changeRoom\",\"roomId\":" + id + "}"); };
        _roomBox.KeyDown += (s, e) => { int id; if (e.Key == Key.Enter && int.TryParse(_roomBox.Text.Trim(), out id) && id > 0) _danmaku.SendMsg("{\"cmd\":\"changeRoom\",\"roomId\":" + id + "}"); };
        roomRow.Children.Add(_roomBox); roomRow.Children.Add(applyBtn);
        _settingsPanel.Children.Add(roomRow);

        // 暂停按钮
        var pauseBtn = new Button { Content = "⏯ Pause", Height = 28, Margin = new Thickness(0, 8, 0, 0), FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(255, 220, 100)), Background = new SolidColorBrush(Color.FromArgb(60, 180, 150, 60)), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(80, 180, 150, 80)), Cursor = Cursors.Hand };
        pauseBtn.Click += (s, e) => { _danmaku.Paused = !_danmaku.Paused; pauseBtn.Content = _danmaku.Paused ? "▶ Resume" : "⏯ Pause"; };
        _settingsPanel.Children.Add(pauseBtn);

        // 防重叠按钮
        var overlapBtn = new Button { Content = "⊞ No Overlap: OFF", Height = 28, Margin = new Thickness(0, 4, 0, 0), FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)), Background = new SolidColorBrush(Color.FromArgb(40, 80, 80, 80)), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(60, 150, 150, 150)), Cursor = Cursors.Hand };
        var overlapOn = false;
        overlapBtn.Click += (s, e) =>
        {
            overlapOn = !overlapOn;
            _danmaku.AntiOverlap = overlapOn;
            overlapBtn.Content = overlapOn ? "⊞ No Overlap: ON" : "⊞ No Overlap: OFF";
            overlapBtn.Foreground = overlapOn ? new SolidColorBrush(Color.FromRgb(100, 255, 200)) : new SolidColorBrush(Color.FromRgb(180, 180, 180));
        };
        _settingsPanel.Children.Add(overlapBtn);

        // 30秒去重按钮
        var dedupBtn = new Button { Content = "⏱ 30s Dedup: OFF", Height = 28, Margin = new Thickness(0, 8, 0, 0), FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)), Background = new SolidColorBrush(Color.FromArgb(40, 80, 80, 80)), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(60, 150, 150, 150)), Cursor = Cursors.Hand };
        var dedupOn = false;
        dedupBtn.Click += (s, e) =>
        {
            dedupOn = !dedupOn;
            _danmaku.SendMsg("{\"cmd\":\"toggleDedup\"}");
            dedupBtn.Content = dedupOn ? "⏱ 30s Dedup: ON" : "⏱ 30s Dedup: OFF";
            dedupBtn.Foreground = dedupOn ? new SolidColorBrush(Color.FromRgb(100, 255, 180)) : new SolidColorBrush(Color.FromRgb(180, 180, 180));
        };
        _settingsPanel.Children.Add(dedupBtn);

        // 清缓存按钮
        var clearBtn = new Button { Content = "🗑 Clear Cache", Height = 28, Margin = new Thickness(0, 4, 0, 0), FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)), Background = new SolidColorBrush(Color.FromArgb(40, 100, 100, 100)), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(60, 150, 150, 150)), Cursor = Cursors.Hand };
        clearBtn.Click += (s, e) => { _danmaku.ClearCache(); _danmaku.SendMsg("{\"cmd\":\"clearCache\"}"); clearBtn.Content = "✓ Cleared"; Task.Delay(1500).ContinueWith(_ => { Dispatcher.Invoke(() => clearBtn.Content = "🗑 Clear Cache"); }); };
        _settingsPanel.Children.Add(clearBtn);

        var exitBtn = new Button { Content = "Exit", Height = 28, Margin = new Thickness(0, 10, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(255, 80, 80)), Background = new SolidColorBrush(Color.FromArgb(60, 255, 60, 60)), BorderThickness = new Thickness(0), Cursor = Cursors.Hand };
        exitBtn.Click += (s, e) => { _danmaku.SendMsg("{\"cmd\":\"shutdown\"}"); UnregisterHotKey(_hwnd, _hotkeyId); Application.Current.Shutdown(); };
        _settingsPanel.Children.Add(exitBtn);
        root.Children.Add(_settingsPanel);

        Loaded += (s, e) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
            SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_LAYERED | WS_EX_TOOLWINDOW);
            var source = HwndSource.FromHwnd(_hwnd);
            source.AddHook(WndProc);
            if (!RegisterHotKey(_hwnd, _hotkeyId, MOD_CONTROL | MOD_SHIFT, 0x7B))
            { _hotkeyId = 9002; RegisterHotKey(_hwnd, _hotkeyId, MOD_CONTROL | MOD_ALT, 0x77); }
        };
        Closed += (s, e) => UnregisterHotKey(_hwnd, _hotkeyId);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY) { ToggleLock(); handled = true; return IntPtr.Zero; }
        return IntPtr.Zero;
    }

    private void ToggleLock()
    {
        _locked = !_locked;
        _gearBtn.Content = _locked ? "🔒" : "⚙";
        _gearBtn.Foreground = _locked
            ? new SolidColorBrush(Color.FromRgb(255, 100, 100))
            : new SolidColorBrush(Color.FromArgb(220, 200, 200, 200));
        var ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        if (_locked)
        {
            SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT);
            if (_settingsPanel.Visibility == Visibility.Visible)
            {
                _settingsPanel.Visibility = Visibility.Collapsed;
                ((Border)Content).Background = Brushes.Transparent;
            }
            Width = 40; Height = 40;
            Left = SystemParameters.PrimaryScreenWidth - 50;
            Top = SystemParameters.PrimaryScreenHeight - 60;
        }
        else
        {
            SetWindowLong(_hwnd, GWL_EXSTYLE, ex & ~WS_EX_TRANSPARENT);
            Width = 40; Height = 40;
            Left = SystemParameters.PrimaryScreenWidth - 70;
            Top = SystemParameters.PrimaryScreenHeight - 60;
        }
    }

    private TextBlock MakeLabel(string text)
    {
        return new TextBlock { Text = text, Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)), FontSize = 12, Margin = new Thickness(10, 6, 0, 2) };
    }
    private Slider MakeSlider(double min, double max, double val)
    {
        return new Slider { Minimum = min, Maximum = max, Value = val, Margin = new Thickness(10, 0, 10, 0), IsSnapToTickEnabled = true, TickFrequency = 1, Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)) };
    }

    private TextBlock MakeLabel(string text, int fs)
    {
        return new TextBlock { Text = text, Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)), FontSize = fs, Margin = new Thickness(10, 2, 0, 1) };
    }

}

// ══════ 入口 ══════
public class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        string wsUrl = args.Length > 0 ? args[0] : "ws://localhost:3000";
        var app = new Application();

        var danmaku = new DanmakuWindow();
        danmaku.Show();

        var control = new ControlWindow(danmaku);
        control.Show();

        // 自启动 node
        Task.Run(() =>
        {
            try { using (var c = new TcpClient("127.0.0.1", 3000)) { return; } } catch { }
            try
            {
                var exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                var rootDir = Path.GetFullPath(Path.Combine(exeDir, ".."));
                var psi = new ProcessStartInfo
                {
                    FileName = Path.Combine(rootDir, "nodejs", "node.exe"),
                    Arguments = "server\\index.js",
                    WorkingDirectory = rootDir,
                    CreateNoWindow = true, UseShellExecute = false,
                };
                Process.Start(psi);
            }
            catch { }
        });

        danmaku.Connect(wsUrl);
        app.Run(control);
    }
}
