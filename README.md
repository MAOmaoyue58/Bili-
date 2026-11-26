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

到 [Releases 页面](../../releases) 下载对应版本的 zip。压缩包里自带 Node.js，不用另外安装任何东西。

## 快速开始

1. 把整个文件夹解压出来（如 `C:\BiliOverlay\`），不要在压缩包里直接运行
2. 双击 `DanmakuOverlay-WE.exe`（OPL 版是 `DanmakuOverlay-OPL.exe`），服务端会在后台自动启动
3. WE版：点齿轮 → 输入直播间号 → 应用（会记住，下次打开自动连接）
4. OPL版：点齿轮 → 填写开放平台凭证 → 保存并连接

## 控制面板

| 功能 | 操作 |
|---|---|
| 设置 | ⚙ 齿轮按钮 |
| 锁定 | **Ctrl+Shift+F12**（游戏时必锁！） |
| 暂停 | ⏯ 暂停 按钮 |
| 防重叠 | ⊞ 防重叠 按钮 |
| 去重 | ⏱ 30 秒去重 按钮 |
| 清缓存 | 🗑 清空缓存 按钮 |
| 颜色 | 10 色块预设，点击改色，再点取消 |
| Cookie（WE版） | 齿轮面板里粘贴 B站 Cookie → 保存，可显示完整用户名（可选） |
| 发弹幕（WE版） | **Ctrl+Shift+Enter** 呼出输入框，Enter 发送，Esc 取消；发到当前设置的直播间，需先保存完整 Cookie |

## 系统要求

- Windows 10/11 x64
- 内存 ~210MB
- 游戏建议「无边框窗口」模式

## 常见问题

弹幕不显示 → 确认直播间正在直播，点 清空缓存

用户名被打码 → WE版在齿轮面板里填写 Cookie

游戏时鼠标显形 → 按 Ctrl+Shift+F12 锁定

程序卡死 → 任务管理器结束 `node.exe` 和 `DanmakuOverlay` 开头的进程

首次运行提示「Windows 已保护你的电脑」→ 程序没有数字签名，点「更多信息」→「仍要运行」

## 构建

C# 浮窗：双击仓库根目录的 `build.bat`（用 .NET Framework 4.x 自带的 csc），生成 `DanmakuOverlay-WE.exe` 和 `DanmakuOverlay-OPL.exe`。

源码结构：

| 文件 | 内容 |
|---|---|
| `src/overlay/Common.cs` | 两个版本共用：弹幕渲染窗、JSON 解析、自动启动服务端 |
| `src/overlay/DanmakuOverlay-Legacy.cs` | WE 版：控制面板、发弹幕输入框、入口 |
| `src/overlay/DanmakuOverlay.cs` | OPL 版：控制面板、入口 |
| `src/shared/protocol.js` | 两个服务端共用的 B站弹幕协议（封包、解包、消息解析） |

手动编译时要把 `Common.cs` 和对应版本的文件一起传给 csc，具体命令见 `build.bat`。

```bash
# Node.js 服务端
npm install
npm run start:we     # WE 版：node src/server-we/index.js
npm run start:opl    # OPL 版：node src/server-opl/opl-service.js
```

配置见 `src/config.js`（端口、默认房间号、Cookie、显示过滤），也可以用环境变量 `BILI_OVERLAY_PORT` / `BILI_ROOM_ID` / `BILI_COOKIE` 覆盖。

浮窗启动时如果本地端口上没有服务端，会自动拉起 node：在 exe 所在目录（及上两级）找 `src\server-*\<脚本>`，优先用同目录下的 `nodejs\node.exe`，没有就用 PATH 里的 `node`。浮窗命令行第一个参数可以指定服务端地址（默认 `ws://localhost:3000`），端口会同步传给自动启动的服务端。

OPL 版在浮窗里保存的凭证写在 `src/opl-settings.json`（明文，已加入 `.gitignore`），不要外传。

### 打包发布

```bash
npm install
npm run package
```

会在 `dist/` 下生成 `BiliOverlay-WE-v<版本>.zip` 和 `BiliOverlay-OPL-v<版本>-preview.zip`，版本号取自 `package.json`。

- 只能在 Windows 上运行：先调用 `build.bat` 编译浮窗，再用系统自带的 `tar.exe` 压缩。
- 每个压缩包都带一份官方 Node.js（版本见 `scripts/package.js`），下载后会校验 SHA256，并缓存在 `.cache/`。
- 只按白名单复制文件，本机设置文件不会进包；打包后还会扫描一遍，发现疑似 Cookie 就中止。
- 压缩包的 SHA256 写在 `dist/SHA256SUMS.txt`。

## 许可证

MIT © 2026 猫猫越
