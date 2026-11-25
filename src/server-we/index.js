// Copyright (c) 2026 猫猫越 <liu2128783468@outlook.com> — Licensed under MIT
// WE 版弹幕服务器 — 全连 B站 comet，共享去重池
const http = require('http');
const https = require('https');
const fs = require('fs');
const path = require('path');
const WebSocket = require('ws');
const { WebSocketServer } = WebSocket;
const config = require('../config');
const { getRealRoomId, getDanmuInfo, ensureCookie, getBuvid, uidFromCookie, checkLogin, cookieValue, sendLiveDanmaku } = require('./bilibili/api');
const { encodePacket, decodePackets, extractDanmaku, isPureEmote } = require('../shared/protocol');

const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36';
const RECONNECT_DELAY_MS = 5000;   // 单台服务器断线 / 房间连接失败后的重试间隔
const HEARTBEAT_MS = 30000;
const HISTORY_POLL_MS = 1500;      // HTTP 补漏间隔
const DEDUP_WINDOW_MS = 30000;     // 「30s 去重」模式的窗口
const DEFAULT_HOST = { host: 'broadcastlv.chat.bilibili.com', port: 2243, wss_port: 443 };

// ── 本机设置持久化（上次的房间号、Cookie）────────────────────
// 保存在 we-settings.json（已加入 .gitignore）。Cookie 是明文的登录凭证，不要把这个文件发给别人。
// 房间号：环境变量 BILI_ROOM_ID > 上次保存的 > config.js
// Cookie：浮窗里保存的 > 环境变量 BILI_COOKIE / config.js
const SETTINGS_FILE = path.join(__dirname, '..', 'we-settings.json');
const baseCookie = config.cookie || '';
let savedSettings = {};

function loadSettings() {
    try {
        if (!fs.existsSync(SETTINGS_FILE)) return;
        const s = JSON.parse(fs.readFileSync(SETTINGS_FILE, 'utf-8'));
        if (s && typeof s === 'object') savedSettings = s;
    } catch (e) {
        console.error('[WE] 读取 ' + SETTINGS_FILE + ' 失败: ' + e.message);
        return;
    }
    const id = parseInt(savedSettings.roomId, 10);
    if (!process.env.BILI_ROOM_ID && Number.isInteger(id) && id > 0) config.roomId = id;
    if (typeof savedSettings.cookie === 'string' && savedSettings.cookie) config.cookie = savedSettings.cookie;
}

function saveSettings(patch) {
    const next = Object.assign({}, savedSettings, patch);
    for (const k of Object.keys(next)) {
        if (next[k] === undefined || next[k] === '') delete next[k];
    }
    try {
        fs.writeFileSync(SETTINGS_FILE, JSON.stringify(next, null, 2), { encoding: 'utf-8', mode: 0o600 });
        savedSettings = next;
        return true;
    } catch (e) {
        console.error('[WE] 保存设置失败: ' + e.message);
        return false;
    }
}

// 用户可能连 "Cookie: " 前缀、引号、换行一起粘贴进来
function sanitizeCookie(raw) {
    return String(raw == null ? '' : raw)
        .replace(/[\r\n]+/g, ' ')
        .trim()
        .replace(/^cookie\s*:\s*/i, '')
        .replace(/^["']|["']$/g, '')
        .trim()
        .replace(/;\s*$/, '');
}

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
// 指纹 = 内容|用户名。多台服务器会推送同一条弹幕，HTTP 补漏也会拉到重复的，统一在这里合并。
// dedupWindowMs = 0：见过就不再显示；> 0：同一指纹在窗口内只显示一次。
const seenFingerprints = new Map();   // 指纹 → 最近一次出现的时间
let dedupWindowMs = 0;

setInterval(() => {
    if (dedupWindowMs > 0) {
        const cutoff = Date.now() - dedupWindowMs;
        for (const [fp, ts] of seenFingerprints) {
            if (ts < cutoff) seenFingerprints.delete(fp);
        }
    } else if (seenFingerprints.size > 2000) {
        // 只删最旧的一半；整表清空会让刚收到的弹幕被另一台服务器的副本再显示一次
        const toDelete = seenFingerprints.size - 1000;
        let i = 0;
        for (const fp of seenFingerprints.keys()) {
            if (i++ >= toDelete) break;
            seenFingerprints.delete(fp);
        }
    }
}, 30000);

function isDuplicate(fp) {
    const last = seenFingerprints.get(fp);
    if (last === undefined) return false;
    return dedupWindowMs > 0 ? Date.now() - last < dedupWindowMs : true;
}

function processDanmaku(d) {
    if (!d || !d.text) return;
    if (!config.showGift && d.isGift) return;
    if (!config.showSC && d.isSC) return;
    if (d.isEnter && !config.showEnter) return;
    const fp = d.text + '|' + (d.username || '');
    if (isDuplicate(fp)) return;
    seenFingerprints.set(fp, Date.now());
    broadcast(d);
}

// ── B站连接 ───────────────────────────────────────────────────
// 每次切换房间都新建一个 session。旧 session 的回调、定时器、重连全部通过 isCurrent() 失效，
// 避免「切房间后旧连接自动重连回来」或「同一房间连出两份连接」。
let session = null;
let sessionGen = 0;

function isCurrent(s) {
    return session === s && s.gen === sessionGen;
}

function addTimeout(s, fn, ms) {
    const t = setTimeout(() => { s.timers.delete(t); fn(); }, ms);
    s.timers.add(t);
}

function addInterval(s, fn, ms) {
    s.timers.add(setInterval(fn, ms));
}

function stopSession() {
    sessionGen++;
    const s = session;
    session = null;
    if (!s) return;
    for (const t of s.timers) { clearTimeout(t); clearInterval(t); }
    s.timers.clear();
    for (const conn of s.conns) {
        if (conn.hb) clearInterval(conn.hb);
        try { conn.ws.close(); } catch (_) {}
    }
}

function makeConnection(s, host, token) {
    const label = host.host + ':' + host.wss_port;
    const headers = { 'User-Agent': UA, 'Origin': 'https://live.bilibili.com' };
    if (s.cookie) headers.Cookie = s.cookie;
    const ws = new WebSocket('wss://' + label + '/sub', { headers });
    const conn = { ws, hb: null, label };
    s.conns.add(conn);

    ws.on('open', () => {
        if (!isCurrent(s)) { ws.close(); return; }
        ws.send(encodePacket(7, JSON.stringify({
            uid: s.uid, roomid: s.realId, protover: 3, buvid: s.buvid, support_ack: true,
            queue_uuid: 'we' + Math.random().toString(36).substring(2, 8),
            scene: 'room', platform: 'web', type: 2, key: token
        }), 1));
        conn.hb = setInterval(() => {
            if (ws.readyState === WebSocket.OPEN) ws.send(encodePacket(2, '', 1));
        }, HEARTBEAT_MS);
        console.log('[WE] ✓ ' + label);
    });

    ws.on('message', (data) => {
        if (!isCurrent(s)) return;
        try {
            const buf = Buffer.isBuffer(data) ? data : Buffer.from(data);
            for (const d of extractDanmaku(decodePackets(buf))) processDanmaku(d);
        } catch (e) {
            console.error('[WE] 消息处理失败: ' + e.message);
        }
    });

    ws.on('close', (code) => {
        if (conn.hb) clearInterval(conn.hb);
        s.conns.delete(conn);
        if (!isCurrent(s)) return;   // 主动切换/退出，不重连
        console.log('[WE] ✗ ' + label + ' code=' + code + '，' + RECONNECT_DELAY_MS / 1000 + 's 后重连');
        addTimeout(s, () => reconnectHost(s, host), RECONNECT_DELAY_MS);
    });

    ws.on('error', (err) => {
        if (isCurrent(s)) console.error('[WE] err ' + label + ': ' + err.message);
    });
}

// 重连前重新获取 token：token 有时效，断线后沿用旧 token 可能鉴权失败
async function reconnectHost(s, host) {
    if (!isCurrent(s)) return;
    try {
        const info = await getDanmuInfo(s.realId, s.cookie);
        if (info.token) s.token = info.token;
    } catch (e) {
        console.error('[WE] 刷新 token 失败，沿用旧 token: ' + e.message);
    }
    if (isCurrent(s)) makeConnection(s, host, s.token);
}

async function connectBili(roomId) {
    stopSession();
    const s = {
        gen: sessionGen, roomId, realId: 0, token: '',
        uid: uidFromCookie(config.cookie), buvid: '', cookie: '',
        conns: new Set(), timers: new Set(),
        historySeen: new Set(), historySeeded: false, polling: false,
    };
    session = s;

    try {
        // getDanmuInfo 要求 Cookie 里带 buvid3，否则返回 -352（风控），所以先把 Cookie 补全
        s.cookie = await ensureCookie(config.cookie);
        if (!isCurrent(s)) return;
        s.buvid = getBuvid(s.cookie);
        s.realId = await getRealRoomId(roomId, s.cookie);
        const info = await getDanmuInfo(s.realId, s.cookie);
        if (!isCurrent(s)) return;
        s.token = info.token;

        // 去重服务器列表
        const seen = new Set();
        const uniqueHosts = [];
        for (const h of info.hosts) {
            const key = h.host + ':' + h.wss_port;
            if (h.host && !seen.has(key)) { seen.add(key); uniqueHosts.push(h); }
        }
        if (uniqueHosts.length === 0) uniqueHosts.push(DEFAULT_HOST);

        console.log('[WE] ' + roomId + ' → ' + s.realId + '  连接' + uniqueHosts.length + '台服务器');
        for (const host of uniqueHosts) makeConnection(s, host, s.token);

        addInterval(s, () => pollHistory(s), HISTORY_POLL_MS);
    } catch (e) {
        if (!isCurrent(s)) return;
        console.error('[WE] 连接房间 ' + roomId + ' 失败: ' + e.message + '，' + RECONNECT_DELAY_MS / 1000 + 's 后重试');
        addTimeout(s, () => { if (isCurrent(s)) connectBili(roomId); }, RECONNECT_DELAY_MS);
    }
}

// ── HTTP 补漏 ─────────────────────────────────────────────────
// gethistory 返回房间最近的十来条弹幕（包括连接之前的旧弹幕）。
// 用「内容|用户|发送时间」标识每一条：第一次拉取只登记不显示，之后只补长连接漏掉的新弹幕，
// 这样旧弹幕不会在去重窗口过期后被反复补播。
function pollHistory(s) {
    if (!isCurrent(s) || s.polling) return;
    s.polling = true;
    const headers = { 'User-Agent': UA, 'Referer': 'https://live.bilibili.com/' + s.realId };
    if (s.cookie) headers.Cookie = s.cookie;
    const url = 'https://api.live.bilibili.com/xlive/web-room/v1/dM/gethistory?roomid=' + s.realId;
    const req = https.get(url, { headers }, (res) => {
        res.setEncoding('utf8');
        let body = '';
        res.on('data', c => body += c);
        res.on('end', () => {
            if (!isCurrent(s)) return;
            try { handleHistory(s, JSON.parse(body)); } catch (_) {}
        });
    });
    req.setTimeout(5000, () => req.destroy());
    req.on('error', () => {});
    req.on('close', () => { s.polling = false; });
}

function handleHistory(s, data) {
    const msgs = (data && data.data && data.data.room) || [];
    const seeding = !s.historySeeded;
    s.historySeeded = true;
    for (const m of msgs) {
        const text = m.text || '';
        const username = m.nickname || '';
        const sentAt = m.timeline || (m.check_info && m.check_info.ts) || '';
        const key = text + '|' + username + '|' + sentAt;
        if (s.historySeen.has(key)) continue;
        s.historySeen.add(key);
        if (seeding || !text || isPureEmote(text)) continue;
        processDanmaku({
            text, mode: 1, fontSize: 25,
            color: 16777215, colorHex: '#ffffff',
            username, uid: m.uid || 0,
            timestamp: Date.now(),
        });
    }
    // 接口每次只返回最近十来条，保留最近 500 个键足够
    while (s.historySeen.size > 500) s.historySeen.delete(s.historySeen.values().next().value);
}

// ── 发弹幕（浮窗里 Ctrl+Shift+Enter 呼出输入框）──────────────
// 用网页端发弹幕接口，发到当前设置的直播间；需要登录 Cookie 里的 SESSDATA 和 bili_jct
const SEND_MIN_INTERVAL_MS = 1000;
let lastSendAt = 0;

const SEND_ERRORS = {
    '-101': 'Cookie 已失效，请重新粘贴',
    '-111': 'bili_jct 校验失败，请粘贴完整的 Cookie',
    '10030': '发送太频繁，请稍后再试',
    '10031': '发送太频繁，请稍后再试',
};

async function handleSendDanmaku(rawText) {
    const text = String(rawText == null ? '' : rawText).replace(/[\r\n]+/g, ' ').trim();
    if (!text) throw new Error('弹幕内容为空');
    if (!(config.roomId > 0)) throw new Error('还没有设置直播间号');
    const cookie = config.cookie || '';
    if (!cookieValue(cookie, 'SESSDATA')) throw new Error('请先在面板里保存登录 Cookie');
    if (!cookieValue(cookie, 'bili_jct')) throw new Error('Cookie 缺少 bili_jct，请粘贴完整的 Cookie');
    if (Date.now() - lastSendAt < SEND_MIN_INTERVAL_MS) throw new Error('发送太快，请稍等一秒');
    lastSendAt = Date.now();

    // 优先用当前连接的真实房间号（短号需要转换）
    const roomId = (session && session.roomId === config.roomId && session.realId)
        || await getRealRoomId(config.roomId, cookie);
    const resp = await sendLiveDanmaku(roomId, text, cookie);
    if (resp.code === undefined) throw new Error('接口返回异常' + (resp._status ? '（HTTP ' + resp._status + '）' : ''));
    if (resp.code !== 0) {
        const msg = SEND_ERRORS[String(resp.code)] || resp.message || resp.msg || ('未知错误 code=' + resp.code);
        console.warn('[WE] 弹幕发送失败（直播间 ' + roomId + '）: ' + msg + ' code=' + resp.code);
        throw new Error(msg);
    }
    // code=0 但 msg 为 f / k 表示被屏蔽词拦截（只有自己能看到）
    const flag = resp.msg || resp.message || '';
    if (flag === 'f') throw new Error('包含敏感词，已被系统屏蔽');
    if (flag === 'k') throw new Error('包含主播设置的屏蔽词');
    console.log('[WE] 已发送弹幕到直播间 ' + roomId + ': ' + text);
    return roomId;
}

// ── Cookie 登录状态（发给浮窗显示，不回传 Cookie 本身）────────
let cookieStatus = { cmd: 'cookieStatus', state: 'none' };

async function refreshCookieStatus() {
    if (!config.cookie) {
        cookieStatus = { cmd: 'cookieStatus', state: 'none' };
    } else {
        try {
            const r = await checkLogin(config.cookie);
            cookieStatus = r.isLogin
                ? { cmd: 'cookieStatus', state: 'ok', uname: r.uname }
                : { cmd: 'cookieStatus', state: 'invalid' };
        } catch (_) {
            cookieStatus = { cmd: 'cookieStatus', state: 'unknown' };
        }
    }
    return cookieStatus;
}

function logCookieStatus(st) {
    if (st.state === 'ok') console.log('[WE] Cookie 已登录: ' + st.uname);
    else if (st.state === 'invalid') console.warn('[WE] ⚠ Cookie 无效或已过期，用户名可能被打码');
    else if (st.state === 'unknown') console.warn('[WE] ⚠ 无法验证 Cookie 登录状态（网络问题？）');
    else console.log('[WE] 未配置 Cookie，用户名可能被打码（可在浮窗齿轮里填写）');
}

// ── C# 浮窗控制协议 ──────────────────────────────────────────
wss.on('connection', (ws) => {
    clients.add(ws);
    // 新连上的浮窗同步当前状态（服务端重启或浮窗重连后按钮状态不会错乱）
    send(ws, { cmd: 'dedupMode', windowMs: dedupWindowMs });
    if (config.roomId > 0) send(ws, { cmd: 'roomChanged', roomId: config.roomId });
    send(ws, cookieStatus);

    ws.on('message', (raw) => {
        let msg;
        try { msg = JSON.parse(raw.toString()); } catch (_) { return; }
        if (!msg || typeof msg !== 'object') return;

        switch (msg.cmd) {
            case 'shutdown':
                console.log('[WE] 收到关闭指令');
                shutdown();
                break;
            case 'toggleDedup':
                dedupWindowMs = dedupWindowMs > 0 ? 0 : DEDUP_WINDOW_MS;
                seenFingerprints.clear();
                broadcast({ cmd: 'dedupMode', windowMs: dedupWindowMs });
                break;
            case 'clearCache':
                seenFingerprints.clear();
                break;
            case 'changeRoom': {
                const id = parseInt(msg.roomId, 10);
                if (!Number.isInteger(id) || id <= 0) {
                    send(ws, { cmd: 'roomError', message: '直播间号无效' });
                    break;
                }
                config.roomId = id;
                saveSettings({ roomId: id });   // 下次启动自动连这个房间
                seenFingerprints.clear();
                connectBili(id);
                broadcast({ cmd: 'roomChanged', roomId: id });
                break;
            }
            case 'sendDanmaku': {
                const reqId = String(msg.reqId || '');
                handleSendDanmaku(msg.text)
                    .then((roomId) => send(ws, { cmd: 'danmakuSent', reqId, ok: true, roomId }))
                    .catch((e) => send(ws, { cmd: 'danmakuSent', reqId, ok: false, message: e.message }));
                break;
            }
            case 'setCookie': {
                // cookie 为空表示清除
                const ck = sanitizeCookie(msg.cookie);
                if (ck && !/=/.test(ck)) {
                    send(ws, { cmd: 'cookieStatus', state: 'error', message: 'Cookie 格式不对' });
                    break;
                }
                if (!saveSettings({ cookie: ck || undefined })) {
                    send(ws, { cmd: 'cookieStatus', state: 'error', message: '保存失败' });
                    break;
                }
                config.cookie = ck || baseCookie;
                console.log(ck ? '[WE] Cookie 已保存' : '[WE] Cookie 已清除' + (baseCookie ? '，改用环境变量 / config.js 里的值' : ''));
                refreshCookieStatus().then((st) => { logCookieStatus(st); broadcast(st); });
                // 用新 Cookie 重连当前房间（uid、buvid、token 都和 Cookie 绑定）
                if (config.roomId > 0) connectBili(config.roomId);
                break;
            }
            default:
                break;
        }
    });
    ws.on('close', () => clients.delete(ws));
    ws.on('error', () => clients.delete(ws));
});

server.on('error', (err) => {
    console.error('[WE] 本地服务启动失败: ' + err.message);
    if (err.code === 'EADDRINUSE') console.error('[WE] 端口 ' + config.serverPort + ' 已被占用，可能已有一个服务端在运行');
    process.exit(1);
});

server.listen(config.serverPort, () => {
    console.log('╔══════════════════════════════════════════╗');
    console.log('║  桌面弹幕 (WE 全连模式)                 ║');
    console.log('║  端口:' + String(config.serverPort).padStart(30) + ' ║');
    console.log('╚══════════════════════════════════════════╝');
    refreshCookieStatus().then((st) => { logCookieStatus(st); broadcast(st); });
    if (config.roomId && config.roomId > 0) {
        if (!process.env.BILI_ROOM_ID && savedSettings.roomId === config.roomId) {
            console.log('[WE] 使用上次的房间号 ' + config.roomId);
        }
        connectBili(config.roomId);
    } else {
        console.log('[WE] 等待浮窗输入房间号...');
    }
});

function shutdown() {
    stopSession();
    wss.close();
    server.close();
    process.exit(0);
}

process.on('SIGINT', shutdown);
process.on('SIGTERM', shutdown);
process.on('unhandledRejection', (e) => console.error('[WE] 未处理的异常: ' + (e && e.message || e)));
