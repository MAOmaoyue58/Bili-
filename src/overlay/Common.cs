// Copyright (c) 2026 猫猫越 <liu2128783468@outlook.com>
// Licensed under MIT — 修改请保留本声明
// 浮窗公共代码：WE 版和 OPL 版共用（弹幕渲染窗、JSON 解析、自动启动服务端）
// 编译时和各版本的入口文件一起编译，见 build.bat
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Interop;
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
    protected readonly List<Dictionary<string, object>> _pendingJson = new List<Dictionary<string, object>>();
    protected const int MaxPending = 200;   // 积压上限，超出丢最旧的
    protected readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
    public Action OnConnected;                                  // 每次连上服务端后调用（UI 线程）
    public Action<Dictionary<string, object>> OnCommand;        // 服务端控制消息（UI 线程）

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
        // 只重建轨道，屏幕上已有的弹幕继续滚完（拖动滑块时不清屏，也不会留下脱离管理的元素）
        _lanes.Clear(); _laneFreeTime = new List<DateTime>(); _laneIndex = 0;
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
            var ws = new ClientWebSocket();
            try
            {
                await ws.ConnectAsync(new Uri(url), CancellationToken.None);
                _ws = ws;
                var onConnected = OnConnected;
                if (onConnected != null) { var op = Dispatcher.BeginInvoke(onConnected); }
                await ReceiveLoop(ws);
            }
            catch { }
            finally
            {
                if (_ws == ws) _ws = null;
                ws.Dispose();
            }
            await Task.Delay(3000);
        }
    }

    // 先把一条 WebSocket 消息的字节收完整再按 UTF-8 解码，
    // 否则一个汉字被拆在两次 Receive 之间就会变成乱码
    protected async Task ReceiveLoop(ClientWebSocket ws)
    {
        var buf = new byte[8192];
        var msg = new MemoryStream();
        while (ws.State == WebSocketState.Open)
        {
            var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
            if (r.MessageType == WebSocketMessageType.Close) break;
            msg.Write(buf, 0, r.Count);
            if (!r.EndOfMessage) continue;
            var data = Encoding.UTF8.GetString(msg.GetBuffer(), 0, (int)msg.Length);
            msg.SetLength(0);
            // 服务端每条消息是一行或多行 JSON
            foreach (var line in data.Split('\n')) HandleLine(line.Trim());
        }
    }

    protected void HandleLine(string line)
    {
        if (line.Length == 0) return;
        var obj = MiniJson.ParseObject(line);
        if (obj == null) return;
        if (obj.ContainsKey("cmd"))
        {
            var handler = OnCommand;
            if (handler != null) Dispatcher.BeginInvoke(new Action(() => handler(obj)));
            return;
        }
        if (Paused) return;
        lock (_pendingJson)
        {
            if (_pendingJson.Count >= MaxPending) _pendingJson.RemoveAt(0);
            _pendingJson.Add(obj);
        }
    }

    protected void Render(Dictionary<string, object> d)
    {
        try
        {
            string text = JStr(d, "text");
            if (text.Length == 0) return;
            string colorHex = string.IsNullOrEmpty(_customColor) ? JStr(d, "colorHex") : _customColor;
            if (colorHex.Length == 0) colorHex = "#ffffff";
            string username = JStr(d, "username");
            double size = _fontSize > 0 ? _fontSize : 12;
            bool isSC = JBool(d, "isSC"), isGift = JBool(d, "isGift");

            string display;
            if (isSC) display = "SC " + username + ": " + text;
            else if (isGift) display = username + " " + text;
            else if (!string.IsNullOrEmpty(username) && username != "unknown" && username != "未知用户") display = username + ": " + text;
            else display = text;

            SolidColorBrush brush;
            if (!_brushCache.TryGetValue(colorHex, out brush))
            {
                Color color;
                try { color = (Color)ColorConverter.ConvertFromString(colorHex); } catch { color = Colors.White; }
                brush = new SolidColorBrush(color);
                brush.Freeze(); // 画刷创建后不再修改，冻结提升渲染性能
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
        // 同屏最多 MaxDanmaku 条，多出来的留在队列里等前面的滚出屏幕
        List<Dictionary<string, object>> batch = null;
        lock (_pendingJson)
        {
            int room = MaxDanmaku - _activeItems.Count;
            if (room > 0 && _pendingJson.Count > 0)
            {
                int n = Math.Min(room, _pendingJson.Count);
                batch = _pendingJson.GetRange(0, n);
                _pendingJson.RemoveRange(0, n);
            }
        }
        if (batch != null) { foreach (var d in batch) Render(d); }

        // 更新弹幕位置
        var now = DateTime.Now; var toRemove = new List<DanmakuItem>();
        foreach (var item in _activeItems)
        {
            double progress = Math.Min((now - item.StartTime).TotalSeconds / item.Duration, 1.0);
            double x = item.StartX + (item.EndX - item.StartX) * progress;
            Canvas.SetLeft(item.Element, x);
            if (progress >= 1.0) toRemove.Add(item);   // 滚到终点（已完全移出屏幕）
        }
        foreach (var item in toRemove) { _activeItems.Remove(item); try { _canvas.Children.Remove(item.Element as UIElement); } catch { } }
    }

    public static string JStr(Dictionary<string, object> d, string key)
    {
        object v;
        if (d == null || !d.TryGetValue(key, out v) || v == null) return "";
        if (v is double) return ((double)v).ToString(CultureInfo.InvariantCulture);
        if (v is bool) return (bool)v ? "true" : "false";
        return v as string ?? "";
    }
    public static bool JBool(Dictionary<string, object> d, string key)
    {
        object v;
        return d != null && d.TryGetValue(key, out v) && v is bool && (bool)v;
    }

    public void SetPlusMode(bool on)
    {
        PlusMode = on;
    }

    // 清掉积压队列和屏幕上所有弹幕，轨道占用一并复位
    public void ClearCache()
    {
        lock (_pendingJson) { _pendingJson.Clear(); }
        foreach (var item in _activeItems) { try { _canvas.Children.Remove(item.Element); } catch { } }
        _activeItems.Clear();
        _brushCache.Clear();
        for (int i = 0; i < _laneFreeTime.Count; i++) _laneFreeTime[i] = DateTime.MinValue;
    }

    public void SendMsg(string msg) { var _ = SendMsgAsync(msg); }

    // ClientWebSocket 不允许并发 SendAsync，用信号量排队；返回是否发送成功
    public async Task<bool> SendMsgAsync(string msg)
    {
        var ws = _ws;
        if (ws == null || ws.State != WebSocketState.Open) return false;
        await _sendLock.WaitAsync();
        try
        {
            var bytes = Encoding.UTF8.GetBytes(msg);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            return true;
        }
        catch { return false; }
        finally { _sendLock.Release(); }
    }
}

// ══════ 极简 JSON：解析服务端发来的一行 JSON；正确处理转义、Unicode 和嵌套 ══════
public static class MiniJson
{
    public static Dictionary<string, object> ParseObject(string s)
    {
        try
        {
            int i = 0;
            var v = ParseValue(s, ref i);
            return v as Dictionary<string, object>;
        }
        catch { return null; }
    }

    static void SkipWs(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

    static object ParseValue(string s, ref int i)
    {
        SkipWs(s, ref i);
        char c = s[i];
        if (c == '{')
        {
            var obj = new Dictionary<string, object>();
            i++; SkipWs(s, ref i);
            if (s[i] == '}') { i++; return obj; }
            while (true)
            {
                SkipWs(s, ref i);
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (s[i] != ':') throw new FormatException();
                i++;
                obj[key] = ParseValue(s, ref i);
                SkipWs(s, ref i);
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return obj; }
                throw new FormatException();
            }
        }
        if (c == '[')
        {
            var list = new List<object>();
            i++; SkipWs(s, ref i);
            if (s[i] == ']') { i++; return list; }
            while (true)
            {
                list.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return list; }
                throw new FormatException();
            }
        }
        if (c == '"') return ParseString(s, ref i);
        if (string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
        if (string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
        if (string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
        int start = i;
        while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
        if (i == start) throw new FormatException();
        return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    static string ParseString(string s, ref int i)
    {
        if (s[i] != '"') throw new FormatException();
        i++;
        var sb = new StringBuilder();
        while (true)
        {
            char c = s[i++];
            if (c == '"') return sb.ToString();
            if (c != '\\') { sb.Append(c); continue; }
            char e = s[i++];
            switch (e)
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'u': sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; break;
                default: sb.Append(e); break;   // \" \\ \/
            }
        }
    }

    // 把任意文本转成可以放进 JSON 双引号里的内容
    public static string Escape(string s)
    {
        if (s == null) return "";
        var sb = new StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}

// ══════ 自动启动本地 node 服务端 ══════
public static class ServerLauncher
{
    // 发布包结构：<根>\server\<脚本>，<根>\nodejs\node.exe；源码仓库结构：<根>\src\<srcDir>\<脚本>
    // 端口取自浮窗的连接地址，通过环境变量 BILI_OVERLAY_PORT 传给服务端，两边保持一致
    public static void EnsureRunning(string wsUrl, string scriptName, string srcDir)
    {
        Uri uri;
        if (!Uri.TryCreate(wsUrl, UriKind.Absolute, out uri) || !uri.IsLoopback) return;   // 连远程服务端时不自启
        int port = uri.Port > 0 ? uri.Port : 3000;
        try { using (new TcpClient("127.0.0.1", port)) { return; } } catch { }   // 已经在运行
        try
        {
            var exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            string script = null, rootDir = null;
            foreach (var dir in new[] { exeDir, Path.Combine(exeDir, ".."), Path.Combine(exeDir, "..", "..") })
            {
                var root = Path.GetFullPath(dir);
                var released = Path.Combine(root, "server", scriptName);
                var source = Path.Combine(root, "src", srcDir, scriptName);
                if (File.Exists(released)) { script = released; rootDir = root; break; }
                if (File.Exists(source)) { script = source; rootDir = root; break; }
            }
            if (script == null) return;
            var bundledNode = Path.Combine(rootDir, "nodejs", "node.exe");
            var psi = new ProcessStartInfo
            {
                FileName = File.Exists(bundledNode) ? bundledNode : "node",   // 没有自带 node 时用 PATH 里的
                Arguments = "\"" + script + "\"",
                WorkingDirectory = rootDir,
                CreateNoWindow = true, UseShellExecute = false,
            };
            psi.EnvironmentVariables["BILI_OVERLAY_PORT"] = port.ToString(CultureInfo.InvariantCulture);
            Process.Start(psi);
        }
        catch { }
    }
}
