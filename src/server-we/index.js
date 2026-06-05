// Copyright (c) 2026 猫猫越 <liu2128783468@outlook.com> — Licensed under MIT
// WE 版弹幕服务器 — 全连 B站 comet，共享去重池
const http = require('http');
const https = require('https');
const { WebSocketServer } = require('ws');
const WebSocket = require('ws');
const config = require('../config');
const { getRealRoomId, getDanmuInfo } = require('./bilibili/api');
const { encodePacket, decodePackets, extractDanmaku } = require('./bilibili/protocol');

const server = http.createServer((_req, res) => { res.writeHead(200); res.end('ok'); });
const wss = new WebSocketServer({ server });
const clients = new Set();

const seenFingerprints = new Map();
let dedupWindowMs = 0;
let biliConnections = [];
let switching = false;
let pollTimer = null;

setInterval(() => {
    if (dedupWindowMs > 0) {
        const cutoff = Date.now() - dedupWindowMs;
        for (const [fp, ts] of seenFingerprints) {
            if (ts < cutoff) seenFingerprints.delete(fp);
        }
    } else if (seenFingerprints.size > 500) {
        seenFingerprints.clear();
    }
}, 30000);

function broadcast(d) {
    const p = JSON.stringify(d) + '\n';
    for (const c of clients) {
        if (c.readyState === 1) { try { c.send(p); } catch (_) { clients.delete(c); } }
    }
}

function processDanmaku(d) {
    if (!config.showGift && d.isGift) return;
    if (!config.showSC && d.isSC) return;
    if (d.isEnter) return;
    const fp = d.text + '|' + d.username;
    if (dedupWindowMs > 0) {
        const lastTs = seenFingerprints.get(fp) || 0;
        if (Date.now() - lastTs < dedupWindowMs) return;
    } else if (seenFingerprints.has(fp)) return;
    seenFingerprints.set(fp, Date.now());
    broadcast(d);
}

function makeConnection(host, realId, token, roomId) {
    const wsUrl = 'wss://' + host.host + ':' + host.wss_port + '/sub';
    const wsOpts = config.cookie ? { headers: { Cookie: config.cookie } } : {};
    const ws = new WebSocket(wsUrl, wsOpts);
    let hb = null;
    const label = host.host + ':' + host.wss_port;

    ws.on('open', () => {
        ws.send(encodePacket(7, JSON.stringify({
            uid: 0, roomid: realId, protover: 3, buvid: '', support_ack: true,
            queue_uuid: 'we' + Math.random().toString(36).substring(2, 8),
            scene: 'room', platform: 'web', type: 2, key: token
        }), 1));
        hb = setInterval(() => {
            if (ws.readyState === WebSocket.OPEN) ws.send(encodePacket(2, '', 1));
        }, 30000);
        console.log('[WE] ✓ ' + label);
    });

    ws.on('message', (data) => {
        const buf = Buffer.isBuffer(data) ? data : Buffer.from(data);
        for (const d of extractDanmaku(decodePackets(buf))) {
            processDanmaku(d);
        }
    });

    ws.on('close', (code) => {
        console.log('[WE] ✗ ' + label + ' code=' + code);
        if (hb) clearInterval(hb);
        biliConnections = biliConnections.filter(c => c.ws !== ws);
        // 非主动切换且房间号未变才重连（防止旧房间残留）
        if (!switching && config.roomId === roomId) {
            setTimeout(() => biliConnections.push(makeConnection(host, realId, token, roomId)), 5000);
        }
    });

    ws.on('error', (err) => console.error('[WE] err ' + label + ': ' + err.message));

    return { ws, hb, host: label };
}

async function connectBili(roomId) {
    switching = true;
    for (const c of biliConnections) {
        try { c.ws.close(); } catch (_) {}
        if (c.hb) clearInterval(c.hb);
    }
    biliConnections = [];
    if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
    switching = false;

    const realId = await getRealRoomId(roomId);
    const info = await getDanmuInfo(realId);

    // 去重服务器列表
    const seen = new Set();
    const uniqueHosts = [];
    for (const h of info.hosts) {
        const key = h.host + ':' + h.wss_port;
        if (!seen.has(key)) { seen.add(key); uniqueHosts.push(h); }
    }

    console.log('[WE] ' + roomId + ' → ' + realId + '  连接' + uniqueHosts.length + '台服务器');

    for (const host of uniqueHosts) {
        biliConnections.push(makeConnection(host, realId, info.token, roomId));
    }

    // HTTP 补漏
    pollTimer = setInterval(() => {
        const opts = config.cookie ? { headers: { Cookie: config.cookie } } : {};
        https.get('https://api.live.bilibili.com/xlive/web-room/v1/dM/gethistory?roomid=' + realId, opts, (res) => {
            let body = '';
            res.on('data', c => body += c);
            res.on('end', () => {
                try {
                    const data = JSON.parse(body);
                    const msgs = (data.data && data.data.room) || [];
                    for (const m of msgs) {
                        const fp = m.text + '|' + (m.nickname || '');
                        if (seenFingerprints.has(fp)) continue;
                        seenFingerprints.set(fp, Date.now());
                        broadcast({
                            text: m.text || '', mode: 1, fontSize: 25,
                            color: 16777215, colorHex: '#ffffff',
                            username: m.nickname || '', uid: m.uid || 0,
                            timestamp: Date.now(),
                        });
                    }
                } catch (_) {}
            });
        }).on('error', () => {});
    }, 1500);
}

wss.on('connection', (ws) => {
    clients.add(ws);
    ws.on('message', (data) => {
        try {
            const msg = JSON.parse(data.toString());
            if (msg.cmd === 'shutdown') {
                console.log('[WE] 收到关闭指令');
                for (const c of biliConnections) { try { c.ws.close(); } catch (_) {} }
                wss.close(); server.close();
                process.exit(0);
            } else if (msg.cmd === 'toggleDedup') {
                dedupWindowMs = dedupWindowMs > 0 ? 0 : 30000;
                seenFingerprints.clear();
                ws.send(JSON.stringify({ cmd: 'dedupMode', windowMs: dedupWindowMs }));
            } else if (msg.cmd === 'clearCache') {
                seenFingerprints.clear();
            } else if (msg.cmd === 'changeRoom' && msg.roomId) {
                config.roomId = parseInt(msg.roomId);
                seenFingerprints.clear();
                connectBili(config.roomId);
                ws.send(JSON.stringify({ cmd: 'roomChanged', roomId: config.roomId }));
            }
        } catch (_) {}
    });
    ws.on('close', () => clients.delete(ws));
    ws.on('error', () => clients.delete(ws));
});

server.listen(config.serverPort, () => {
    console.log('╔══════════════════════════════════════════╗');
    console.log('║  桌面弹幕 (WE 全连模式)                 ║');
    console.log('║  端口:' + String(config.serverPort).padStart(30) + ' ║');
    console.log('╚══════════════════════════════════════════╝');
    if (config.roomId && config.roomId > 0) {
        connectBili(config.roomId);
    } else {
        console.log('[WE] 等待浮窗输入房间号...');
    }
});

process.on('SIGINT', () => {
    for (const c of biliConnections) { try { c.ws.close(); } catch (_) {} }
    wss.close(); server.close();
    process.exit(0);
});
