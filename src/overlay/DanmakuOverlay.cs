// Copyright (c) 2026 猫猫越 <liu2128783468@outlook.com>
// Licensed under MIT — 修改请保留本声明
// OPL 版浮窗 — 控制面板、入口
// 弹幕渲染窗、JSON 解析、自动启动服务端等公共代码在 Common.cs，编译时需一并带上（见 build.bat）
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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

// ══════ 控制面板窗（右下角、正常交互、隐身模式下不显示）══════
public class ControlWindow : Window
{
    private DanmakuWindow _danmaku;
    private bool _locked;
    private double _speed = 10, _fontSize = 28;
    private int _laneCount = 15, _regionMode;
    private string _customColor;
    private TextBox _akiBox, _appIdBox, _codeBox;
    private PasswordBox _aksBox;
    private TextBlock _oplStatus;
    private Button _gearBtn, _dedupBtn;
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
        // 中文界面优先用微软雅黑；按钮上的符号（⚙ ⏯ ⊞ 等）回退到 Segoe UI Symbol / Emoji
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI Symbol, Segoe UI Emoji");
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
            Width = open ? 260 : 40; Height = open ? 640 : 40;
            Left = SystemParameters.PrimaryScreenWidth - (open ? 270 : 50);
            Top = open ? SystemParameters.PrimaryScreenHeight - 640 : SystemParameters.PrimaryScreenHeight - 60;
            // 面板打开时允许获取焦点（否则无法打字）
            var ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
            if (open) SetWindowLong(_hwnd, GWL_EXSTYLE, ex & ~WS_EX_NOACTIVATE);
            else SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE);
        };

        _settingsPanel.Children.Add(MakeLabel("滚动时长：10 秒"));
        var spdSlider = MakeSlider(1, 20, 10);
        _settingsPanel.Children.Add(spdSlider);
        var spdLabel = _settingsPanel.Children[_settingsPanel.Children.Count - 2] as TextBlock;
        spdSlider.ValueChanged += (s, e) => { _speed = spdSlider.Value; spdLabel.Text = "滚动时长：" + (int)_speed + " 秒"; _danmaku.ApplySettings(_speed, _fontSize, _laneCount, _regionMode, _customColor); };

        _settingsPanel.Children.Add(MakeLabel("字号：28px"));
        var fntSlider = MakeSlider(12, 48, 28);
        _settingsPanel.Children.Add(fntSlider);
        var fntLabel = _settingsPanel.Children[_settingsPanel.Children.Count - 2] as TextBlock;
        fntSlider.ValueChanged += (s, e) => { _fontSize = fntSlider.Value; fntLabel.Text = "字号：" + (int)_fontSize + "px"; _danmaku.ApplySettings(_speed, _fontSize, _laneCount, _regionMode, _customColor); };

        _settingsPanel.Children.Add(MakeLabel("轨道数：15"));
        var laneSlider = MakeSlider(1, 30, 15);
        _settingsPanel.Children.Add(laneSlider);
        var lanesLbl = _settingsPanel.Children[_settingsPanel.Children.Count - 2] as TextBlock;
        laneSlider.ValueChanged += (s, e) => { _laneCount = (int)laneSlider.Value; lanesLbl.Text = "轨道数：" + _laneCount; _danmaku.ApplySettings(_speed, _fontSize, _laneCount, _regionMode, _customColor); };

        _settingsPanel.Children.Add(MakeLabel("显示区域："));
        var regRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 2, 0, 0) };
        var btnTop = new Button { Content = "顶部 1/5", Width = 70, Height = 24, FontSize = 10, Background = new SolidColorBrush(Color.FromArgb(60, 60, 60, 60)), Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(60, 100, 100, 100)), Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 5, 0) };
        var btnBot = new Button { Content = "底部 1/5", Width = 70, Height = 24, FontSize = 10, Background = new SolidColorBrush(Color.FromArgb(60, 60, 60, 60)), Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(60, 100, 100, 100)), Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 5, 0) };
        btnTop.Click += (s, e) => { _regionMode = _regionMode == 1 ? 0 : 1; btnTop.Background = _regionMode == 1 ? new SolidColorBrush(Color.FromArgb(120, 0, 180, 220)) : new SolidColorBrush(Color.FromArgb(60, 60, 60, 60)); _danmaku.ApplySettings(_speed, _fontSize, _laneCount, _regionMode, _customColor); };
        btnBot.Click += (s, e) => { _regionMode = _regionMode == 2 ? 0 : 2; btnBot.Background = _regionMode == 2 ? new SolidColorBrush(Color.FromArgb(120, 0, 180, 220)) : new SolidColorBrush(Color.FromArgb(60, 60, 60, 60)); _danmaku.ApplySettings(_speed, _fontSize, _laneCount, _regionMode, _customColor); };
        regRow.Children.Add(btnTop); regRow.Children.Add(btnBot);
        _settingsPanel.Children.Add(regRow);

        _settingsPanel.Children.Add(MakeLabel("颜色（再点一次取消）："));
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

        _settingsPanel.Children.Add(MakeLabel("开放平台凭证：", 10));
        _settingsPanel.Children.Add(MakeLabel("accessKeyId:", 9));
        _akiBox = new TextBox { Width = 220, Height = 22, FontSize = 10, Background = new SolidColorBrush(Color.FromArgb(80, 40, 40, 40)), Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(60, 100, 100, 100)), VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 2) };
        _settingsPanel.Children.Add(_akiBox);
        _settingsPanel.Children.Add(MakeLabel("accessKeySecret:", 9));
        _aksBox = new PasswordBox { Width = 220, Height = 22, FontSize = 10, Background = new SolidColorBrush(Color.FromArgb(80, 40, 40, 40)), Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(60, 100, 100, 100)), VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 2) };
        _settingsPanel.Children.Add(_aksBox);
        _settingsPanel.Children.Add(MakeLabel("项目 ID（appId）：", 9));
        _appIdBox = new TextBox { Width = 220, Height = 22, FontSize = 10, Background = new SolidColorBrush(Color.FromArgb(80, 40, 40, 40)), Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(60, 100, 100, 100)), VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 2) };
        _settingsPanel.Children.Add(_appIdBox);
        _settingsPanel.Children.Add(MakeLabel("主播身份码（code）：", 9));
        _codeBox = new TextBox { Width = 220, Height = 22, FontSize = 10, Background = new SolidColorBrush(Color.FromArgb(80, 40, 40, 40)), Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(60, 100, 100, 100)), VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 2) };
        _settingsPanel.Children.Add(_codeBox);

        var oplSaveBtn = new Button { Content = "保存并连接", Height = 28, Margin = new Thickness(10, 6, 10, 4), FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(100, 220, 255)), Background = new SolidColorBrush(Color.FromArgb(80, 30, 150, 200)), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(100, 100, 200, 255)), Cursor = Cursors.Hand };
        oplSaveBtn.Click += (s, e) =>
        {
            // 所有字段都按 JSON 字符串转义；appId 也作为字符串发送，由服务端校验
            var msg = "{\"cmd\":\"setOplConfig\"," +
                      "\"accessKeyId\":\"" + MiniJson.Escape(_akiBox.Text.Trim()) + "\"," +
                      "\"accessKeySecret\":\"" + MiniJson.Escape(_aksBox.Password.Trim()) + "\"," +
                      "\"appId\":\"" + MiniJson.Escape(_appIdBox.Text.Trim()) + "\"," +
                      "\"code\":\"" + MiniJson.Escape(_codeBox.Text.Trim()) + "\"}";
            SetOplStatus("正在保存…", false);
            _danmaku.SendMsgAsync(msg).ContinueWith(t =>
            {
                if (!t.Result) Dispatcher.BeginInvoke(new Action(() => SetOplStatus("✗ 未连接到服务端", true)));
            });
        };
        _settingsPanel.Children.Add(oplSaveBtn);
        _oplStatus = new TextBlock { FontSize = 9, Margin = new Thickness(10, 0, 10, 2), TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)) };
        _settingsPanel.Children.Add(_oplStatus);

        // 暂停按钮
        var pauseBtn = new Button { Content = "⏯ 暂停", Height = 28, Margin = new Thickness(0, 8, 0, 0), FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(255, 220, 100)), Background = new SolidColorBrush(Color.FromArgb(60, 180, 150, 60)), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(80, 180, 150, 80)), Cursor = Cursors.Hand };
        pauseBtn.Click += (s, e) => { _danmaku.Paused = !_danmaku.Paused; pauseBtn.Content = _danmaku.Paused ? "▶ 继续" : "⏯ 暂停"; };
        _settingsPanel.Children.Add(pauseBtn);

        // 防重叠按钮
        var overlapBtn = new Button { Content = "⊞ 防重叠：关", Height = 28, Margin = new Thickness(0, 4, 0, 0), FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)), Background = new SolidColorBrush(Color.FromArgb(40, 80, 80, 80)), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(60, 150, 150, 150)), Cursor = Cursors.Hand };
        var overlapOn = false;
        overlapBtn.Click += (s, e) =>
        {
            overlapOn = !overlapOn;
            _danmaku.AntiOverlap = overlapOn;
            overlapBtn.Content = overlapOn ? "⊞ 防重叠：开" : "⊞ 防重叠：关";
            overlapBtn.Foreground = overlapOn ? new SolidColorBrush(Color.FromRgb(100, 255, 200)) : new SolidColorBrush(Color.FromRgb(180, 180, 180));
        };
        _settingsPanel.Children.Add(overlapBtn);

        // 30秒去重按钮
        var dedupBtn = new Button { Content = "⏱ 30 秒去重：关", Height = 28, Margin = new Thickness(0, 4, 0, 0), FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)), Background = new SolidColorBrush(Color.FromArgb(40, 80, 80, 80)), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(60, 150, 150, 150)), Cursor = Cursors.Hand };
        _dedupBtn = dedupBtn;
        // 按钮状态以服务端回传的 dedupMode 为准，避免和服务端不同步
        dedupBtn.Click += (s, e) => _danmaku.SendMsg("{\"cmd\":\"toggleDedup\"}");
        _settingsPanel.Children.Add(dedupBtn);

        // 清缓存按钮
        var clearBtn = new Button { Content = "🗑 清空缓存", Height = 28, Margin = new Thickness(0, 4, 0, 0), FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)), Background = new SolidColorBrush(Color.FromArgb(40, 100, 100, 100)), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(60, 150, 150, 150)), Cursor = Cursors.Hand };
        clearBtn.Click += (s, e) => { _danmaku.ClearCache(); _danmaku.SendMsg("{\"cmd\":\"clearCache\"}"); clearBtn.Content = "✓ 已清空"; Task.Delay(1500).ContinueWith(_ => { Dispatcher.Invoke(() => clearBtn.Content = "🗑 清空缓存"); }); };
        _settingsPanel.Children.Add(clearBtn);

        var exitBtn = new Button { Content = "退出", Height = 28, Margin = new Thickness(0, 10, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(255, 80, 80)), Background = new SolidColorBrush(Color.FromArgb(60, 255, 60, 60)), BorderThickness = new Thickness(0), Cursor = Cursors.Hand };
        exitBtn.Click += async (s, e) =>
        {
            exitBtn.IsEnabled = false;
            // 等关闭指令真正发出去再退出，否则 node 进程会残留
            await Task.WhenAny(_danmaku.SendMsgAsync("{\"cmd\":\"shutdown\"}"), Task.Delay(1000));
            UnregisterHotKey(_hwnd, _hotkeyId);
            Application.Current.Shutdown();
        };
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

    // 服务端发来的控制消息（UI 线程）
    public void HandleCommand(Dictionary<string, object> m)
    {
        switch (DanmakuWindow.JStr(m, "cmd"))
        {
            case "oplConfig":
                _akiBox.Text = DanmakuWindow.JStr(m, "accessKeyId");
                var appId = DanmakuWindow.JStr(m, "appId");
                _appIdBox.Text = appId == "0" ? "" : appId;
                _codeBox.Text = DanmakuWindow.JStr(m, "code");
                // 服务端不回传密钥，只告诉我们是否已保存过
                if (DanmakuWindow.JBool(m, "credSaved")) SetOplStatus("密钥已保存，留空则沿用", false);
                break;
            case "oplConfigSaved":
                _aksBox.Password = "";
                SetOplStatus("✓ 已保存，正在连接…", false);
                break;
            case "oplConfigError":
                SetOplStatus("✗ " + DanmakuWindow.JStr(m, "message"), true);
                break;
            case "dedupMode":
                SetDedupState(DanmakuWindow.JStr(m, "windowMs") != "0");
                break;
        }
    }

    private void SetOplStatus(string text, bool error)
    {
        if (_oplStatus == null) return;
        _oplStatus.Text = text;
        _oplStatus.Foreground = new SolidColorBrush(error ? Color.FromRgb(255, 110, 110) : Color.FromRgb(150, 200, 150));
    }

    private void SetDedupState(bool on)
    {
        if (_dedupBtn == null) return;
        _dedupBtn.Content = on ? "⏱ 30 秒去重：开" : "⏱ 30 秒去重：关";
        _dedupBtn.Foreground = on ? new SolidColorBrush(Color.FromRgb(100, 255, 180)) : new SolidColorBrush(Color.FromRgb(180, 180, 180));
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
            // 锁定时如果面板开着会被收起，同时恢复 NOACTIVATE，避免抢游戏焦点
            SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
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
        danmaku.OnCommand = control.HandleCommand;
        // 每次（重）连上服务端都拉一次已保存的凭证
        danmaku.OnConnected = () => danmaku.SendMsg("{\"cmd\":\"getOplConfig\"}");
        control.Show();

        // 自启动 node
        Task.Run(() => ServerLauncher.EnsureRunning(wsUrl, "opl-service.js", "server-opl"));

        var connectLoop = danmaku.Connect(wsUrl);   // 后台常驻的连接/重连循环
        app.Run(control);
    }
}
