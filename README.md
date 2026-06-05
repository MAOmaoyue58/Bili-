# BiliOverlay — B站直播弹幕透明浮窗

双窗口 + WS_EX_TRANSPARENT 物理隔离，玩 3D 全屏游戏鼠标零干扰。全连 live-comet 弹幕引擎，零门槛零注册。

## 版本

| | WE-极致优化 | OPL-官方全量 |
|---|---|---|
| 弹幕源 | live-comet 全连 | 开放平台官方 |
| 覆盖率 | ~90% | 100% |
| 门槛 | **零门槛** | 需注册开发者 |
| 适用 | 看任何人 | 主播自用 |

## 下载

[Releases 页面](https://github.com/maoyue/BiliOverlay/releases) 下载对应版本 zip，解压双击 `start.bat`。

## 快速开始

1. 解压到英文路径（如 `C:\BiliOverlay\`）
2. 双击 `start.bat`（或直接双击 `overlay\DanmakuOverlay.exe`）
3. WE版：点齿轮 → 输入房间号 → Apply
4. OPL版：点齿轮 → 填写开放平台凭证 → Save & Connect

## 控制面板

| 功能 | 操作 |
|---|---|
| 设置 | ⚙ 齿轮按钮 |
| 锁定 | **Ctrl+Shift+F12**（游戏时必锁！） |
| 暂停 | ⏯ Pause 按钮 |
| 防重叠 | ⊞ No Overlap 按钮 |
| 去重 | ⏱ 30s Dedup 按钮 |
| 清缓存 | 🗑 Clear Cache 按钮 |
| 颜色 | 10 色块预设，点击改色，再点取消 |

## 系统要求

- Windows 10/11 x64
- 内存 ~210MB
- 游戏建议「无边框窗口」模式

## 常见问题

弹幕不显示 → 确认直播间正在直播，点 Clear Cache

游戏时鼠标显形 → 按 Ctrl+Shift+F12 锁定

程序卡死 → 任务管理器结束 `node.exe` 和 `DanmakuOverlay.exe`

## 构建

```bash
# C# 浮窗（需要 .NET 4.x）
csc /target:winexe /out:DanmakuOverlay.exe \
    /reference:PresentationFramework.dll \
    /reference:PresentationCore.dll \
    /reference:WindowsBase.dll \
    /reference:System.Xaml.dll \
    DanmakuOverlay.cs

# Node.js 服务器
npm install ws
node server/index.js
```

## 许可证

MIT © 2026 猫猫越
