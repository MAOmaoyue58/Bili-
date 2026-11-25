// Copyright (c) 2026 猫猫越 <liu2128783468@outlook.com> — Licensed under MIT
// B站直播开放平台 — 弹幕服务
// 用户注册一次开发者 → 在浮窗齿轮里填 4 个值 → 全量弹幕
const http = require('http');
const fs = require('fs');
const path = require('path');
const WebSocket = require('ws');
const { WebSocketServer } = WebSocket;
const config = require('../config');
const { encodePacket, decodePackets, isPureEmote, normalizeColor } = require('../shared/protocol');
const openPlatform = require('./bilibili/open-platform');

const RECONNECT_DELAY_MS = 5000;
const HEARTBEAT_MS = 30000;        // 开放平台要求 20~60s 内发一次心跳
const DEDUP_WINDOW_MS = 30000;
const SECRET = 'accessKeySecret';  // 密钥字段名

// ── 开放平台凭证持久化 ────────────────────────────────────────
// 注意：密钥以明文保存在本机 opl-settings.json（已加入 .gitignore），不要把这个文件发给别人。
const SETTINGS_FILE = path.join(__dirname, '..', 'opl-settings.json');
config.openPlatform = Object.assign({ accessKeyId: '', [SECRET]: '', appId: 0, code: '' }, config.openPlatform);

function loadSettings() {
    try {
        if (!fs.existsSync(SETTINGS_FILE)) return;
        const saved = JSON.parse(fs.readFileSync(SETTINGS_FILE, 'utf-8'));
        const cfg = config.openPlatform;
        if (saved.accessKeyId) cfg.accessKeyId = String(saved.accessKeyId);
        if (saved[SECRET]) cfg[SECRET] = String(saved[SECRET]);
        if (saved.appId) cfg.appId = parseInt(saved.appId, 10) || 0;
        if (saved.code) cfg.code = String(saved.code);
    } catch (e) {
        console.error('[OPL] 读取 ' + SETTINGS_FILE + ' 失败: ' + e.message);
    }
}

function saveSettings(opl) {
    try {
        fs.writeFileSync(SETTINGS_FILE, JSON.stringify(opl, null, 2), { encoding: 'utf-8', mode: 0o600 });
    } catch (e) {
        console.error('[OPL] 保存设置失败: ' + e.message);
        return false;
    }
    Object.assign(config.openPlatform, opl);
    return true;
}

function hasCredentials() {
    const c = config.openPlatform;
    return !!(c.accessKeyId && c[SECRET] && c.appId && c.code);
}

function printSetupHint() {
    console.warn('[OPL] ⚠ 未配置开放平台凭证');
    console.warn('[OPL]   1. 注册: https://open-live.bilibili.com/open-register-form/personal');
    console.warn('[OPL]   2. 审核通过后获取 accessKeyId / 密钥 / appId 和主播身份码');
    console.warn('[OPL]   3. 在浮窗齿轮里填写后点 Save & Connect（或填入 config.js → openPlatform）');
}

// 加载已保存的凭证
loadSettings();

// ── 本地 WS 服务器（C# 浮窗连接）───────────────────────────────
// 每条消息都是一行 JSON，以 \n 结尾
const server = http.createServer((_req, res) => { res.writeHead(200); res.end('ok'); });
const wss = new WebSocketServer({ server });
const clients = new Set();

function sendRaw(ws, payload) {
    if (ws.readyState !== WebSocket.OPEN) return;
    try { ws.send(payload); } catch (_) { clients.delete(ws); }
}

function send(ws, obj) {
    sendRaw(ws, JSON.stringify(obj) + '\n');
}

function broadcast(obj) {
    const payload = JSON.stringify(obj) + '\n';
    for (const c of clients) sendRaw(c, payload);
}

// ── 去重 ───────────────────────────────────────────────────────
const seenFingerprints = new Map();   // 指纹 → 最近一次出现的时间
let dedupWindowMs = 0;

setInterval(() => {
    if (dedupWindowMs > 0) {
        const cutoff = Date.now() - dedupWindowMs;
        for (const [fp, ts] of seenFingerprints) {
            if (ts < cutoff) seenFingerprints.delete(fp);
        }
    } else if (seenFingerprints.size > 2000) {
        const toDelete = seenFingerprints.size - 1000;
        let i = 0;
        for (const fp of seenFingerprints.keys()) {
            if (i++ >= toDelete) break;
            seenFingerprints.delete(fp);
        }
    }
}, 30000);

// ── 弹幕格式转换（开放平台 → 浮窗标准格式）────────────────────
function convertDanmaku(dm) {
    return {
        text: dm.msg || '',
        username: dm.uname || '',
        mode: 1,
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
        text: (g.gift_name || '') + ' ×' + (g.gift_num || g.num || 1),
        username: g.uname || '',
        mode: 4, fontSize: 20,
        color: 0xFF69B4, colorHex: '#ff69b4',
        uid: 0, isGift: true,
        timestamp: (g.timestamp || 0) * 1000,
        price: g.price || 0,
    };
}

// 开放平台的 SC 消息不带颜色字段，按 B站 SC 档位取色
function scTierHex(rmb) {
    if (rmb >= 2000) return 'ab1a32';
    if (rmb >= 1000) return 'e54d4d';
    if (rmb >= 500) return 'e09443';
    if (rmb >= 100) return 'e2b52b';
    if (rmb >= 50) return '427d9e';
    return '2a60b2';
}

function convertSC(sc) {
    const rmb = Number(sc.rmb || sc.price || 0);
    return {
        text: sc.message || '',
        username: sc.uname || '',
        mode: 4, fontSize: 22,
        ...normalizeColor(sc.background_price_color || scTierHex(rmb), 'ffffff'),
        uid: 0, isSC: true,
        timestamp: (sc.start_time || sc.timestamp || 0) * 1000,
        price: rmb,
    };
}

function convertGuard(g) {
    return {
        text: '开通了' + (g.guard_level === 1 ? '总督' : g.guard_level === 2 ? '提督' : '舰长'),
        username: (g.user_info && g.user_info.uname) || g.uname || '',
        mode: 4, fontSize: 20,
        color: 0x00CCFF, colorHex: '#00ccff',
        uid: 0,
        timestamp: (g.timestamp || 0) * 1000,
    };
}

function convertEnter(e) {
    return {
        text: '进入了直播间',
        username: e.uname || '',
        mode: 1, fontSize: 20,
        color: 0x00CCFF, colorHex: '#00ccff',
        uid: 0, isEnter: true,
        timestamp: (e.timestamp || 0) * 1000,
    };
}

// ── 弹幕过滤与去重 ─────────────────────────────────────────────
function processMessage(d) {
    if (!d || !d.text) return;
    if (!config.showGift && d.isGift) return;
    if (!config.showSC && d.isSC) return;
    if (d.isEnter && !config.showEnter) return;
    const fp = d.text + '|' + d.username;
    if (dedupWindowMs > 0) {
        const lastTs = seenFingerprints.get(fp) || 0;
        if (Date.now() - lastTs < dedupWindowMs) return;
    } else if (seenFingerprints.has(fp)) return;
    seenFingerprints.set(fp, Date.now());
    broadcast(d);
}

// ── WebSocket 消息处理 ─────────────────────────────────────────
// 开放平台的消息名有 LIVE_OPEN_PLATFORM_DM 和 OPEN_LIVEROOM_DM 两种写法，统一只看后缀
function cmdSuffix(cmd) {
    return String(cmd).replace(/^(LIVE_OPEN_PLATFORM_|OPEN_LIVEROOM_)/, '');
}

function handleMessage(msg) {
    if (msg._type === 'auth_reply') {
        if (msg.code === 0) console.log('[OPL] ✓ 鉴权成功，开始接收弹幕');
        else console.error('[OPL] ✗ 鉴权失败: ' + JSON.stringify(msg));
        return;
    }
    if (!msg.cmd || !msg.data) return;
    const data = msg.data;

    switch (cmdSuffix(msg.cmd)) {
        case 'DM':
            if (data.msg && !isPureEmote(data.msg)) processMessage(convertDanmaku(data));
            break;
        case 'SEND_GIFT':
            processMessage(convertGift(data));
            break;
        case 'SUPER_CHAT':
            processMessage(convertSC(data));
            break;
        case 'GUARD':
            processMessage(convertGuard(data));
            break;
        case 'LIVE_ROOM_ENTER':
            processMessage(convertEnter(data));
            break;
        case 'INTERACT_WORD':
            if (data.msg_type === 1) processMessage(convertEnter(data));
            break;
        default:
            // SUPER_CHAT_DEL / LIKE / LIVE_START / LIVE_END 等暂不处理
            break;
    }
}

function handleWsMessage(data) {
    const buf = Buffer.isBuffer(data) ? data : Buffer.from(data);
    for (const msg of decodePackets(buf)) {
        if (!msg) continue;
        try { handleMessage(msg); }
        catch (e) { console.error('[OPL] 消息处理失败: ' + e.message); }
    }
}

// ── 主连接逻辑 ─────────────────────────────────────────────────
// connGen 在每次断开时递增，旧连接的回调和定时器据此失效，
// 保证任何时候只有一条连接，也不会出现「主动断开后又被自动重连」。
let connGen = 0;
let shuttingDown = false;
let currentGameId = null;
let currentWs = null;
let hbTimer = null;
let httpHbTimer = null;
let reconnectTimer = null;

function scheduleReconnect(gen) {
    if (shuttingDown || gen !== connGen) return;
    console.warn('[OPL] ' + RECONNECT_DELAY_MS / 1000 + ' 秒后重连...');
    clearTimeout(reconnectTimer);
    reconnectTimer = setTimeout(() => {
        reconnectTimer = null;
        if (shuttingDown || gen !== connGen) return;
        connectOpenPlatform().catch(e => console.error('[OPL] 重连失败: ' + e.message));
    }, RECONNECT_DELAY_MS);
}

// 断开当前连接，并调用 end 结束开放平台项目
async function disconnect() {
    connGen++;
    clearTimeout(reconnectTimer); reconnectTimer = null;
    clearInterval(hbTimer); hbTimer = null;
    clearInterval(httpHbTimer); httpHbTimer = null;

    const ws = currentWs;
    currentWs = null;
    if (ws) { try { ws.close(); } catch (_) {} }

    const gameId = currentGameId;
    currentGameId = null;
    if (gameId) {
        try { await openPlatform.end(config.openPlatform, gameId); }
        catch (e) { console.warn('[OPL] 结束项目失败: ' + e.message); }
    }
}

// 每次连接（包括自动重连）都先断开并 end 旧项目，再 start 新项目
async function connectOpenPlatform() {
    await disconnect();
    if (shuttingDown) return;
    const gen = connGen;

    if (!hasCredentials()) {
        printSetupHint();
        return;
    }

    console.log('[OPL] 启动项目...');
    let info;
    try {
        info = await openPlatform.start(config.openPlatform);
    } catch (e) {
        console.error('[OPL] 启动失败: ' + e.message);
        scheduleReconnect(gen);
        return;
    }

    if (gen !== connGen || shuttingDown) {
        // 启动期间又触发了重连或退出：这个项目已经没人用了，直接结束
        try { await openPlatform.end(config.openPlatform, info.gameId); } catch (_) {}
        return;
    }

    currentGameId = info.gameId;
    console.log('[OPL] 项目已启动  game_id=' + currentGameId);

    // HTTP 心跳（保持项目存活）
    httpHbTimer = setInterval(() => {
        openPlatform.heartbeat(config.openPlatform, info.gameId)
            .catch(e => console.warn('[OPL] 心跳失败: ' + e.message));
    }, HEARTBEAT_MS);

    const wsUrl = (info.wssLinks || [])[0];
    if (!wsUrl) {
        console.error('[OPL] 开放平台没有返回 WebSocket 地址');
        scheduleReconnect(gen);
        return;
    }

    console.log('[OPL] 连接 WebSocket...');
    const ws = new WebSocket(wsUrl);
    currentWs = ws;

    ws.on('open', () => {
        if (gen !== connGen) return;
        console.log('[OPL] WS 已连接，发送鉴权...');
        // authBody 是 JSON 字符串，用 op=7 封包发送
        ws.send(encodePacket(7, info.authBody, 1));
        hbTimer = setInterval(() => {
            if (ws.readyState === WebSocket.OPEN) ws.send(encodePacket(2, '', 1));
        }, HEARTBEAT_MS);
    });

    ws.on('message', (data) => {
        if (gen === connGen) handleWsMessage(data);
    });

    ws.on('close', (code) => {
        if (gen !== connGen) return;   // 主动断开（改凭证 / 重连 / 退出）
        clearInterval(hbTimer); hbTimer = null;
        currentWs = null;
        console.warn('[OPL] WS 断开  code=' + code);
        scheduleReconnect(gen);
    });

    ws.on('error', (err) => {
        if (gen === connGen) console.error('[OPL] WS 错误: ' + err.message);
    });
}

// ── C# 浮窗控制协议 ──────────────────────────────────────────
function handleSetOplConfig(ws, msg) {
    const cfg = config.openPlatform;
    const next = {
        accessKeyId: String(msg.accessKeyId || '').trim(),
        // 浮窗不回显密钥，留空表示沿用已保存的值
        [SECRET]: String(msg[SECRET] || '').trim() || cfg[SECRET] || '',
        appId: parseInt(msg.appId, 10) || 0,
        code: String(msg.code || '').trim(),
    };
    const missing = Object.keys(next).filter(k => !next[k]);
    if (missing.length) {
        send(ws, { cmd: 'oplConfigError', message: '缺少：' + missing.join('、') });
        return;
    }
    if (!saveSettings(next)) {
        send(ws, { cmd: 'oplConfigError', message: '保存失败' });
        return;
    }
    console.log('[OPL] 凭证已保存，重启连接...');
    send(ws, { cmd: 'oplConfigSaved' });
    connectOpenPlatform().catch(e => console.error('[OPL] 连接失败: ' + e.message));
}

wss.on('connection', (ws) => {
    clients.add(ws);
    console.log('[本地] 浮窗连接 +1  共' + clients.size + '个');
    send(ws, { cmd: 'dedupMode', windowMs: dedupWindowMs });

    ws.on('message', (raw) => {
        let msg;
        try { msg = JSON.parse(raw.toString()); } catch (_) { return; }
        if (!msg || typeof msg !== 'object') return;

        switch (msg.cmd) {
            case 'shutdown':
                console.log('[OPL] 收到关闭指令');
                shutdown();
                break;
            case 'getOplConfig': {
                // 回传已保存的配置（不含密钥，只告诉浮窗是否已保存过）
                const cfg = config.openPlatform;
                send(ws, {
                    cmd: 'oplConfig',
                    accessKeyId: cfg.accessKeyId || '',
                    appId: cfg.appId || 0,
                    code: cfg.code || '',
                    credSaved: !!cfg[SECRET],
                });
                break;
            }
            case 'setOplConfig':
                handleSetOplConfig(ws, msg);
                break;
            case 'toggleDedup':
                dedupWindowMs = dedupWindowMs > 0 ? 0 : DEDUP_WINDOW_MS;
                seenFingerprints.clear();
                broadcast({ cmd: 'dedupMode', windowMs: dedupWindowMs });
                break;
            case 'clearCache':
                seenFingerprints.clear();
                console.log('[OPL] 缓存已清');
                break;
            default:
                // changeRoom 等：开放平台按身份码绑定房间，无需处理
                break;
        }
    });

    ws.on('close', () => { clients.delete(ws); });
    ws.on('error', () => { clients.delete(ws); });
});

// ── 启动 ─────────────────────────────────────────────────────
server.on('error', (err) => {
    console.error('[OPL] 本地服务启动失败: ' + err.message);
    if (err.code === 'EADDRINUSE') console.error('[OPL] 端口 ' + config.serverPort + ' 已被占用，可能已有一个服务端在运行');
    process.exit(1);
});

server.listen(config.serverPort, () => {
    console.log('╔══════════════════════════════════════════╗');
    console.log('║  桌面弹幕（开放平台模式）               ║');
    console.log('║  端口:' + String(config.serverPort).padStart(30) + ' ║');
    console.log('╚══════════════════════════════════════════╝');

    if (hasCredentials()) {
        connectOpenPlatform().catch(err => console.error('[OPL] 启动失败: ' + err.message));
    } else {
        printSetupHint();
    }
});

// ── 优雅退出 ─────────────────────────────────────────────────
async function shutdown() {
    if (shuttingDown) return;
    shuttingDown = true;
    console.log('\n[OPL] 正在退出...');
    const forceExit = setTimeout(() => process.exit(0), 3000);   // end 接口卡住时兜底
    try { await disconnect(); } catch (_) {}
    clearTimeout(forceExit);
    wss.close();
    server.close();
    process.exit(0);
}

process.on('SIGINT', shutdown);
process.on('SIGTERM', shutdown);
process.on('unhandledRejection', (e) => console.error('[OPL] 未处理的异常: ' + (e && e.message || e)));
