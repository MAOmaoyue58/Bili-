// Copyright (c) 2026 猫猫越 <liu2128783468@outlook.com>
// Licensed under MIT — 修改请保留本声明
// WE 版浮窗 — 控制面板、发弹幕输入框、入口
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
    private TextBox _roomBox;
    private Button _gearBtn, _dedupBtn;
    private Brush _roomBoxBorder;
    private PasswordBox _cookieBox;
    private TextBlock _cookieStatus;
    private TextBlock _hotkeyHint;
    private DanmakuInputWindow _input;
    private int _roomId;
    private bool _sendHotkeyOk;
    private StackPanel _settingsPanel;

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    const uint MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_ALT = 0x0001;
    const int WM_HOTKEY = 0x0312, GWL_EXSTYLE = -20;
    const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20;
    int _hotkeyId = 9001;
    const int SendHotkeyId = 9003;          // Ctrl+Shift+Enter 发弹幕
    const uint MOD_NOREPEAT = 0x4000, VK_RETURN = 0x0D;
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
            Width = open ? 260 : 40; Height = open ? 610 : 40;
            Left = SystemParameters.PrimaryScreenWidth - (open ? 270 : 50);
            Top = open ? SystemParameters.PrimaryScreenHeight - 670 : SystemParameters.PrimaryScreenHeight - 60;
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

        _settingsPanel.Children.Add(MakeLabel("直播间号："));
        var roomRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 2, 0, 0) };
        _roomBox = new TextBox { Width = 140, Height = 22, FontSize = 11, Background = new SolidColorBrush(Color.FromArgb(80, 40, 40, 40)), Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(60, 100, 100, 100)), VerticalContentAlignment = VerticalAlignment.Center };
        var applyBtn = new Button { Content = "应用", Width = 50, Height = 22, FontSize = 10, Background = new SolidColorBrush(Color.FromArgb(60, 60, 60, 60)), Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(60, 100, 100, 100)), Cursor = Cursors.Hand, Margin = new Thickness(5, 0, 0, 0) };
        applyBtn.Click += (s, e) => { int id; if (int.TryParse(_roomBox.Text.Trim(), out id) && id > 0) _danmaku.SendMsg("{\"cmd\":\"changeRoom\",\"roomId\":" + id + "}"); };
        _roomBox.KeyDown += (s, e) => { int id; if (e.Key == Key.Enter && int.TryParse(_roomBox.Text.Trim(), out id) && id > 0) _danmaku.SendMsg("{\"cmd\":\"changeRoom\",\"roomId\":" + id + "}"); };
        roomRow.Children.Add(_roomBox); roomRow.Children.Add(applyBtn);
        _settingsPanel.Children.Add(roomRow);
        _roomBoxBorder = _roomBox.BorderBrush;

        // Cookie：保存在服务端本机的 we-settings.json，服务端不回传，只告诉浮窗登录状态
        _settingsPanel.Children.Add(MakeLabel("Cookie（可选，用于显示完整用户名）：", 10));
        var cookieRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 2, 0, 0) };
        _cookieBox = new PasswordBox { Width = 120, Height = 22, FontSize = 11, Background = new SolidColorBrush(Color.FromArgb(80, 40, 40, 40)), Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(60, 100, 100, 100)), VerticalContentAlignment = VerticalAlignment.Center };
        var cookieSaveBtn = new Button { Content = "保存", Width = 45, Height = 22, FontSize = 10, Background = new SolidColorBrush(Color.FromArgb(60, 60, 60, 60)), Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(60, 100, 100, 100)), Cursor = Cursors.Hand, Margin = new Thickness(5, 0, 0, 0) };
        var cookieClearBtn = new Button { Content = "清除", Width = 45, Height = 22, FontSize = 10, Background = new SolidColorBrush(Color.FromArgb(60, 60, 60, 60)), Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(60, 100, 100, 100)), Cursor = Cursors.Hand, Margin = new Thickness(5, 0, 0, 0) };
        cookieSaveBtn.Click += (s, e) => SendCookie(false);
        _cookieBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) SendCookie(false); };
        cookieClearBtn.Click += (s, e) => SendCookie(true);
        cookieRow.Children.Add(_cookieBox); cookieRow.Children.Add(cookieSaveBtn); cookieRow.Children.Add(cookieClearBtn);
        _settingsPanel.Children.Add(cookieRow);
        _cookieStatus = new TextBlock { FontSize = 9, Margin = new Thickness(10, 1, 10, 2), TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)) };
        _settingsPanel.Children.Add(_cookieStatus);
        _hotkeyHint = new TextBlock { FontSize = 9, Margin = new Thickness(10, 0, 10, 2), TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)) };
        _settingsPanel.Children.Add(_hotkeyHint);

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
        var dedupBtn = new Button { Content = "⏱ 30 秒去重：关", Height = 28, Margin = new Thickness(0, 8, 0, 0), FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)), Background = new SolidColorBrush(Color.FromArgb(40, 80, 80, 80)), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(60, 150, 150, 150)), Cursor = Cursors.Hand };
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
            UnregisterHotKey(_hwnd, SendHotkeyId);
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
            _sendHotkeyOk = RegisterHotKey(_hwnd, SendHotkeyId, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_RETURN);
            UpdateHotkeyHint();
        };
        Closed += (s, e) => { UnregisterHotKey(_hwnd, _hotkeyId); UnregisterHotKey(_hwnd, SendHotkeyId); };
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            if (wParam.ToInt32() == SendHotkeyId) ShowDanmakuInput();
            else ToggleLock();
            handled = true;
            return IntPtr.Zero;
        }
        return IntPtr.Zero;
    }

    // 服务端发来的控制消息（UI 线程）
    public void HandleCommand(Dictionary<string, object> m)
    {
        switch (DanmakuWindow.JStr(m, "cmd"))
        {
            case "roomChanged":
                int.TryParse(DanmakuWindow.JStr(m, "roomId"), out _roomId);
                if (!_roomBox.IsKeyboardFocusWithin) _roomBox.Text = DanmakuWindow.JStr(m, "roomId");
                _roomBox.BorderBrush = _roomBoxBorder;
                break;
            case "roomError":
                _roomBox.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 90, 90));
                break;
            case "danmakuSent":
                if (_input != null) _input.OnResult(DanmakuWindow.JStr(m, "reqId"), DanmakuWindow.JBool(m, "ok"), DanmakuWindow.JStr(m, "message"));
                break;
            case "cookieStatus":
            {
                var state = DanmakuWindow.JStr(m, "state");
                if (state == "ok") SetCookieStatus("✓ 已登录：" + DanmakuWindow.JStr(m, "uname"), false);
                else if (state == "invalid") SetCookieStatus("✗ Cookie 无效或已过期，用户名可能被打码", true);
                else if (state == "unknown") SetCookieStatus("Cookie 已保存（无法验证登录状态，请检查网络）", false);
                else if (state == "error") SetCookieStatus("✗ " + DanmakuWindow.JStr(m, "message"), true);
                else SetCookieStatus("未填写 Cookie，用户名可能被打码", false);
                break;
            }
            case "dedupMode":
                SetDedupState(DanmakuWindow.JStr(m, "windowMs") != "0");
                break;
        }
    }

    // clear = true 表示清除已保存的 Cookie
    private void SendCookie(bool clear)
    {
        var ck = clear ? "" : _cookieBox.Password.Trim();
        if (!clear && ck.Length == 0) { SetCookieStatus("请先粘贴 Cookie", true); return; }
        SetCookieStatus(clear ? "正在清除…" : "正在验证…", false);
        _danmaku.SendMsgAsync("{\"cmd\":\"setCookie\",\"cookie\":\"" + MiniJson.Escape(ck) + "\"}").ContinueWith(t =>
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (t.Result) _cookieBox.Clear();   // 发送成功后清空输入框，结果以服务端回传的状态为准
                else SetCookieStatus("✗ 未连接到服务端", true);
            }));
        });
    }

    private void SetCookieStatus(string text, bool error)
    {
        if (_cookieStatus == null) return;
        _cookieStatus.Text = text;
        _cookieStatus.Foreground = new SolidColorBrush(error ? Color.FromRgb(255, 110, 110) : Color.FromRgb(150, 200, 150));
    }

    private void UpdateHotkeyHint()
    {
        if (_hotkeyHint == null) return;
        _hotkeyHint.Text = _sendHotkeyOk ? "发弹幕：Ctrl+Shift+Enter（需保存完整 Cookie）" : "✗ 发弹幕快捷键 Ctrl+Shift+Enter 被其他程序占用";
        _hotkeyHint.Foreground = new SolidColorBrush(_sendHotkeyOk ? Color.FromRgb(150, 150, 150) : Color.FromRgb(255, 110, 110));
    }

    private void ShowDanmakuInput()
    {
        if (_input == null)
        {
            _input = new DanmakuInputWindow(_danmaku);
            _input.Closed += (s, e) => _input = null;   // 被 Alt+F4 关掉后，下次重新创建
        }
        _input.ShowFor(_roomId);
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

// ══════ 发弹幕输入框（Ctrl+Shift+Enter 呼出，Enter 发送，Esc 取消）══════
public class DanmakuInputWindow : Window
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);

    private readonly DanmakuWindow _danmaku;
    private readonly TextBox _box;
    private readonly TextBlock _hint;
    private IntPtr _prevForeground = IntPtr.Zero;   // 呼出前的前台窗口（通常是游戏），收起时把焦点还回去
    private string _pendingReqId;
    private int _reqSeq;
    private bool _hidingBySelf;

    public DanmakuInputWindow(DanmakuWindow danmaku)
    {
        _danmaku = danmaku;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = 560; Height = 78;
        Background = new SolidColorBrush(Color.FromRgb(24, 24, 24));
        BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 80));
        BorderThickness = new Thickness(1);
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI Symbol, Segoe UI Emoji");

        var root = new StackPanel { Margin = new Thickness(10, 7, 10, 8) };
        _hint = new TextBlock { FontSize = 11, Margin = new Thickness(2, 0, 0, 4), TextTrimming = TextTrimming.CharacterEllipsis, Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)) };
        _box = new TextBox
        {
            FontSize = 16, Height = 32, Padding = new Thickness(4, 0, 4, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromRgb(40, 40, 40)),
            Foreground = new SolidColorBrush(Color.FromRgb(230, 230, 230)),
            CaretBrush = new SolidColorBrush(Color.FromRgb(230, 230, 230)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(90, 90, 90)),
            BorderThickness = new Thickness(1),
        };
        root.Children.Add(_hint);
        root.Children.Add(_box);
        Content = root;

        // 输入法组字时按 Enter / Esc 收到的是 Key.ImeProcessed，不会误发送
        _box.PreviewKeyDown += (s, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; HideAndRestore(); }
            else if (e.Key == Key.Enter) { e.Handled = true; Send(); }
        };
        // 点到别的窗口就收起，焦点留在用户点的地方
        Deactivated += (s, e) => { if (!_hidingBySelf && IsVisible) Hide(); };
    }

    public void ShowFor(int roomId)
    {
        if (!IsVisible)
        {
            var fg = GetForegroundWindow();
            if (fg != new WindowInteropHelper(this).Handle) _prevForeground = fg;
        }
        if (_pendingReqId == null)
        {
            if (roomId > 0) SetHint("发送到直播间 " + roomId + "　·　Enter 发送　·　Esc 取消", false);
            else SetHint("还没有设置直播间号，请先在面板里设置", true);
        }
        var wa = SystemParameters.WorkArea;
        Left = wa.Left + (wa.Width - Width) / 2;
        Top = wa.Bottom - Height - 120;
        Show();
        Activate();
        SetForegroundWindow(new WindowInteropHelper(this).Handle);   // 热键消息带有前台权限，可以抢到焦点
        _box.Focus();
        Keyboard.Focus(_box);
        _box.CaretIndex = _box.Text.Length;
    }

    private void Send()
    {
        if (_pendingReqId != null) return;   // 上一条还没发完
        var text = _box.Text.Trim();
        if (text.Length == 0) { HideAndRestore(); return; }
        var reqId = "d" + (++_reqSeq);
        _pendingReqId = reqId;
        _box.IsReadOnly = true;
        SetHint("发送中…", false);
        _danmaku.SendMsgAsync("{\"cmd\":\"sendDanmaku\",\"reqId\":\"" + reqId + "\",\"text\":\"" + MiniJson.Escape(text) + "\"}").ContinueWith(t =>
        {
            if (!t.Result) Dispatcher.BeginInvoke(new Action(() => OnResult(reqId, false, "未连接到服务端")));
        });
        // 超时兜底，避免一直卡在"发送中"
        Task.Delay(10000).ContinueWith(_ => Dispatcher.BeginInvoke(new Action(() => OnResult(reqId, false, "发送超时，请检查网络"))));
    }

    // 服务端回传的发送结果（UI 线程）
    public void OnResult(string reqId, bool ok, string message)
    {
        if (reqId != _pendingReqId) return;   // 已经处理过（超时或重复）
        _pendingReqId = null;
        _box.IsReadOnly = false;
        if (ok)
        {
            _box.Clear();
            SetHint("✓ 已发送", false);
            var seq = _reqSeq;
            // 停半秒让用户看到结果，再收起并把焦点还给游戏
            Task.Delay(500).ContinueWith(_ => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (seq == _reqSeq && _pendingReqId == null && _box.Text.Length == 0 && IsVisible) HideAndRestore();
            })));
        }
        else
        {
            // 失败时保留输入内容，方便修改后重发
            SetHint("✗ " + (string.IsNullOrEmpty(message) ? "发送失败" : message), true);
            _box.CaretIndex = _box.Text.Length;
        }
    }

    private void HideAndRestore()
    {
        _hidingBySelf = true;
        Hide();
        _hidingBySelf = false;
        if (_prevForeground != IntPtr.Zero) SetForegroundWindow(_prevForeground);
        _prevForeground = IntPtr.Zero;
    }

    private void SetHint(string text, bool error)
    {
        _hint.Text = text;
        _hint.Foreground = new SolidColorBrush(error ? Color.FromRgb(255, 110, 110) : Color.FromRgb(150, 200, 150));
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
        control.Show();

        // 自启动 node
        Task.Run(() => ServerLauncher.EnsureRunning(wsUrl, "index.js", "server-we"));

        var connectLoop = danmaku.Connect(wsUrl);   // 后台常驻的连接/重连循环
        app.Run(control);
    }
}
