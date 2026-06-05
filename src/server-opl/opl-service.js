// Copyright (c) 2026 猫猫越 <liu2128783468@outlook.com> — Licensed under MIT
// B站直播开放平台 — 弹幕服务
// 用户注册一次开发者 → 在浮窗齿轮里填 4 个值 → 全量弹幕
const http = require('http');
const fs = require('fs');
const path = require('path');
const { WebSocketServer } = require('ws');
const WebSocket = require('ws');
const config = require('../config');
const { encodePacket, decodePackets } = require('./bilibili/protocol');
const openPlatform = require('./bilibili/open-platform');

// ── 开放平台凭证持久化 ────────────────────────────────────────
const SETTINGS_FILE = path.join(__dirname, '..', 'opl-settings.json');

function loadSettings() {
    try {
        if (fs.existsSync(SETTINGS_FILE)) {
            const saved = JSON.parse(fs.readFileSync(SETTINGS_FILE, 'utf-8'));
            if (saved.accessKeyId) config.openPlatform.accessKeyId = saved.accessKeyId;
            if (saved.accessKeySecret) config.openPlatform.accessKeySecret = saved.accessKeySecret;
            if (saved.appId) config.openPlatform.appId = saved.appId;
            if (saved.code) config.openPlatform.code = saved.code;
        }
    } catch (_) {}
}

function saveSettings(opl) {
    try {
        fs.writeFileSync(SETTINGS_FILE, JSON.stringify(opl, null, 2), 'utf-8');
        config.openPlatform.accessKeyId = opl.accessKeyId;
        config.openPlatform.accessKeySecret = opl.accessKeySecret;
        config.openPlatform.appId = opl.appId;
        config.openPlatform.code = opl.code;
    } catch (e) {
        console.error('[OPL] 保存设置失败: ' + e.message);
    }
}

// 加载已保存的凭证
loadSettings();

// ── 本地 WS 服务器（C# 浮窗连接）───────────────────────────────
const server = http.createServer((_req, res) => { res.writeHead(200); res.end('ok'); });
const wss = new WebSocketServer({ server });
const clients = new Set();

const seenFingerprints = new Map();
let dedupWindowMs = 0;
let stats = { total: 0, lastMin: 0, lastCount: 0 };
setInterval(() => {
    stats.lastMin = stats.total - stats.lastCount;
    stats.lastCount = stats.total;
}, 60000);

setInterval(() => {
    if (dedupWindowMs > 0) {
        const cutoff = Date.now() - dedupWindowMs;
        for (const [fp, ts] of seenFingerprints) {
            if (ts < cutoff) seenFingerprints.delete(fp);
        }
    } else if (seenFingerprints.size > 2000) {
        const toDelete = seenFingerprints.size - 1000;
        let i = 0;
        for (const [fp] of seenFingerprints) {
            if (i++ >= toDelete) break;
            seenFingerprints.delete(fp);
        }
    }
}, 30000);

function broadcast(d) {
    const p = JSON.stringify(d) + '\n';
    for (const c of clients) {
        if (c.readyState === 1) { try { c.send(p); } catch (_) { clients.delete(c); } }
    }
}

// ── 弹幕格式转换（开放平台 → 浮窗标准格式）────────────────────
function convertDanmaku(dm) {
    return {
        text: dm.msg || '',
        username: dm.uname || '',
        mode: dm.dm_type === 1 ? 0 : 1,
        fontSize: 25,
        color: 16777215,
        colorHex: '#ffffff',
        uid: 0, // 开放平台不暴露 uid
        timestamp: (dm.timestamp || 0) * 1000,
        fansMedal: dm.fans_medal_name || '',
        fansLevel: dm.fans_medal_level || 0,
        guardLevel: dm.guard_level || 0,
    };
}

function convertGift(g) {
    return {
        text: (g.gift_name || '') + ' ×' + (g.num || 1),
        username: g.uname || '',
        mode: 4, fontSize: 20,
        color: 0xFF69B4, colorHex: '#ff69b4',
        uid: 0, isGift: true,
        timestamp: (g.timestamp || 0) * 1000,
        price: g.price || 0,
    };
}

function convertSC(sc) {
    return {
        text: sc.message || '',
        username: sc.uname || '',
        mode: 4, fontSize: 22,
        color: parseInt(sc.background_price_color || '16777215'),
        colorHex: '#' + (sc.background_price_color || 'ffffff'),
        uid: 0, isSC: true,
        timestamp: (sc.start_time || sc.timestamp || 0) * 1000,
        price: sc.price || 0,
    };
}

function convertGuard(g) {
    return {
        text: '开通了' + (g.guard_level === 1 ? '总督' : g.guard_level === 2 ? '提督' : '舰长'),
        username: g.user_info?.uname || g.uname || '',
        mode: 4, fontSize: 20,
        color: 0x00CCFF, colorHex: '#00ccff',
        uid: 0,
        timestamp: (g.timestamp || 0) * 1000,
    };
}

function isEnterMsg(dm) {
    // 开放平台没有单独的 enter 消息，OPEN_LIVEROOM_INTERACT_WORD 进房消息
    return false;
}

// ── 弹幕过滤与去重 ─────────────────────────────────────────────
function processMessage(d) {
    if (!config.showGift && d.isGift) return;
    if (!config.showSC && d.isSC) return;
    if (d.isEnter) return;
    const fp = d.text + '|' + d.username;
    if (dedupWindowMs > 0) {
        const lastTs = seenFingerprints.get(fp) || 0;
        if (Date.now() - lastTs < dedupWindowMs) return;
    } else if (seenFingerprints.has(fp)) return;
    seenFingerprints.set(fp, Date.now());
    stats.total++;
    broadcast(d);
}

// ── WebSocket 消息处理 ─────────────────────────────────────────
function handleWsMessage(data) {
    const buf = Buffer.isBuffer(data) ? data : Buffer.from(data);
    for (const msg of decodePackets(buf)) {
        if (!msg || !msg.cmd) continue;

        if (msg.cmd === 'OPEN_LIVEROOM_DM' && msg.data) {
            if (!config.showEnter || true) {
                const d = convertDanmaku(msg.data);
                if (d.text) processMessage(d);
            }
        } else if (msg.cmd === 'OPEN_LIVEROOM_SEND_GIFT' && msg.data) {
            const d = convertGift(msg.data);
            processMessage(d);
        } else if (msg.cmd === 'OPEN_LIVEROOM_SUPER_CHAT' && msg.data) {
            const d = convertSC(msg.data);
            processMessage(d);
        } else if (msg.cmd === 'OPEN_LIVEROOM_SUPER_CHAT_DEL' && msg.data) {
            // SC 撤销，可以忽略或标记
        } else if (msg.cmd === 'OPEN_LIVEROOM_GUARD' && msg.data) {
            const d = convertGuard(msg.data);
            processMessage(d);
        } else if (msg.cmd === 'OPEN_LIVEROOM_INTERACT_WORD' && msg.data) {
            if (config.showEnter && msg.data.msg_type === 1) {
                processMessage({
                    text: '进入了直播间',
                    username: msg.data.uname || '',
                    mode: 1, fontSize: 20,
                    color: 0x00CCFF, colorHex: '#00ccff',
                    uid: 0, isEnter: true,
                    timestamp: (msg.data.timestamp || 0) * 1000,
                });
            }
        }
    }
}

// ── 主连接逻辑 ─────────────────────────────────────────────────
let currentGameId = null;
let currentWs = null;
let hbTimer = null;
let httpHbTimer = null;

async function connectOpenPlatform() {
    if (!config.openPlatform || !config.openPlatform.accessKeyId) {
        console.error('[OPL] ✗ 请先在 config.js 填写开放平台凭证');
        console.error('[OPL]   注册: https://open-live.bilibili.com/open-register-form/personal');
        return;
    }

    console.log('[OPL] 启动项目...');
    let info;
    try {
        info = await openPlatform.start(config.openPlatform);
    } catch (e) {
        console.error('[OPL] 启动失败: ' + e.message);
        console.error('[OPL] 5秒后重试...');
        setTimeout(connectOpenPlatform, 5000);
        return;
    }

    currentGameId = info.gameId;
    console.log('[OPL] 项目已启动  game_id=' + currentGameId);

    // HTTP 心跳（保持项目存活）
    if (httpHbTimer) clearInterval(httpHbTimer);
    httpHbTimer = setInterval(async () => {
        if (!currentGameId) return;
        try { await openPlatform.heartbeat(config.openPlatform, currentGameId); }
        catch (_) {}
    }, 30000);

    // 连接 WebSocket
    const wsUrl = info.wssLinks[0];
    console.log('[OPL] 连接 WebSocket...');

    const ws = new WebSocket(wsUrl);
    currentWs = ws;

    ws.on('open', () => {
        console.log('[OPL] WS 已连接，发送鉴权...');
        // authBody 是 JSON 字符串，用 op=7 封包发送
        ws.send(encodePacket(7, info.authBody, 1));

        // WS 心跳
        if (hbTimer) clearInterval(hbTimer);
        hbTimer = setInterval(() => {
            if (ws.readyState === WebSocket.OPEN) {
                ws.send(encodePacket(2, '', 1));
            }
        }, 30000);
    });

    ws.on('message', (data) => {
        handleWsMessage(data);
    });

    ws.on('close', (code) => {
        console.warn('[OPL] WS 断开  code=' + code + ' 5秒后重连...');
        if (hbTimer) clearInterval(hbTimer);
        currentWs = null;
        setTimeout(connectOpenPlatform, 5000);
    });

    ws.on('error', (err) => {
        console.error('[OPL] WS 错误: ' + err.message);
    });
}

async function disconnect() {
    if (hbTimer) clearInterval(hbTimer);
    if (httpHbTimer) clearInterval(httpHbTimer);
    if (currentWs) {
        try { currentWs.close(); } catch (_) {}
        currentWs = null;
    }
    if (currentGameId) {
        try { await openPlatform.end(config.openPlatform, currentGameId); } catch (_) {}
        currentGameId = null;
    }
}

// ── C# 浮窗控制协议 ──────────────────────────────────────────
wss.on('connection', (ws) => {
    clients.add(ws);
    console.log('[本地] 浮窗连接 +1  共' + clients.size + '个');

    ws.on('message', (data) => {
        try {
            const msg = JSON.parse(data.toString());

            if (msg.cmd === 'shutdown') {
                console.log('[OPL] 收到关闭指令');
                shutdown();
            } else if (msg.cmd === 'getOplConfig') {
                // 回传已保存的配置（不含 secret）
                const cfg = config.openPlatform || {};
                ws.send(JSON.stringify({
                    cmd: 'oplConfig',
                    accessKeyId: cfg.accessKeyId || '',
                    appId: cfg.appId || 0,
                    code: cfg.code || '',
                }));
            } else if (msg.cmd === 'setOplConfig') {
                saveSettings({
                    accessKeyId: msg.accessKeyId || '',
                    accessKeySecret: msg.accessKeySecret || '',
                    appId: parseInt(msg.appId) || 0,
                    code: msg.code || '',
                });
                console.log('[OPL] 凭证已保存，重启连接...');
                // 断开旧连接，重新连开放平台
                disconnect().then(() => connectOpenPlatform()).catch(() => {});
                ws.send(JSON.stringify({ cmd: 'oplConfigSaved' }));
            } else if (msg.cmd === 'toggleDedup') {
                dedupWindowMs = dedupWindowMs > 0 ? 0 : 30000;
                seenFingerprints.clear();
                ws.send(JSON.stringify({ cmd: 'dedupMode', windowMs: dedupWindowMs }));
            } else if (msg.cmd === 'clearCache') {
                seenFingerprints.clear();
                console.log('[OPL] 缓存已清');
            } else if (msg.cmd === 'changeRoom' && msg.roomId) {
                // 降级模式才需要，开放平台不需要换房间
            }
        } catch (_) {}
    });

    ws.on('close', () => { clients.delete(ws); });
    ws.on('error', () => { clients.delete(ws); });
});

// ── 启动 ─────────────────────────────────────────────────────
server.listen(config.serverPort, () => {
    console.log('╔══════════════════════════════════════════╗');
    console.log('║  桌面弹幕（开放平台模式）               ║');
    console.log('║  端口:' + String(config.serverPort).padStart(30) + ' ║');
    console.log('╚══════════════════════════════════════════╝');

    if (!config.openPlatform || !config.openPlatform.accessKeyId) {
        console.warn('[OPL] ⚠ 未配置开放平台凭证，请编辑 config.js');
        console.warn('[OPL]   1. 注册: https://open-live.bilibili.com/open-register-form/personal');
        console.warn('[OPL]   2. 审核通过后获取 access_key + 身份码');
        console.warn('[OPL]   3. 填入 config.js → openPlatform 字段');
    } else {
        connectOpenPlatform().catch(err => {
            console.error('[OPL] 启动失败: ' + err.message);
        });
    }
});

// ── 优雅退出 ─────────────────────────────────────────────────
async function shutdown() {
    console.log('\n[OPL] 正在退出...');
    await disconnect();
    wss.close();
    server.close();
    process.exit(0);
}

process.on('SIGINT', shutdown);
process.on('SIGTERM', shutdown);
