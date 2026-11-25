// Copyright (c) 2026 猫猫越 <liu2128783468@outlook.com> — Licensed under MIT
// 运行配置：WE 版（server-we）和 OPL 版（server-opl）共用
// 部分配置项也可以用环境变量覆盖；浮窗自动启动服务端时会传入 BILI_OVERLAY_PORT

function intEnv(name, fallback) {
    const v = parseInt(process.env[name], 10);
    return Number.isInteger(v) && v > 0 ? v : fallback;
}

module.exports = {
    // 本地 WebSocket 端口，需和浮窗连接地址一致（默认 ws://localhost:3000）
    serverPort: intEnv('BILI_OVERLAY_PORT', 3000),

    // ── WE 版 ──
    // 启动后自动连接的房间号，0 表示等浮窗里输入
    roomId: intEnv('BILI_ROOM_ID', 0),
    // 可选：B站登录 Cookie（含 SESSDATA / buvid3 / DedeUserID）。
    // 不填也能用，但 B站可能对未登录连接隐去部分用户名
    cookie: process.env.BILI_COOKIE || '',

    // ── 显示过滤（两个版本通用）──
    showGift: true,
    showSC: true,
    showEnter: false,

    // ── OPL 版：B站直播开放平台凭证 ──
    // 推荐在浮窗齿轮里填写，会保存到 src/opl-settings.json 并覆盖这里的值。
    // 也可以在这里补上 accessKeySecret 字段直接写死（不要把含密钥的文件提交到仓库）。
    openPlatform: {
        accessKeyId: '',
        appId: 0,
        code: '',
    },
};
