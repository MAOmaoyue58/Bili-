// B站直播开放平台 API — HMAC-SHA256 签名 + 项目启停 + 心跳
const crypto = require('crypto');
const https = require('https');

const BASE_HOST = 'open-live.bilibili.com';

function md5(str) {
    return crypto.createHash('md5').update(str, 'utf-8').digest('hex');
}

function hmacSha256(key, str) {
    return crypto.createHmac('sha256', key).update(str, 'utf-8').digest('hex');
}

function buildHeaders(accessKeyId, accessKeySecret, bodyStr) {
    const timestamp = Math.floor(Date.now() / 1000).toString();
    const nonce = crypto.randomUUID();
    const contentMd5 = md5(bodyStr);

    const signStr = [
        `x-bili-accesskeyid:${accessKeyId}`,
        `x-bili-content-md5:${contentMd5}`,
        `x-bili-signature-method:HMAC-SHA256`,
        `x-bili-signature-nonce:${nonce}`,
        `x-bili-signature-version:1.0`,
        `x-bili-timestamp:${timestamp}`,
    ].join('\n');

    const sig = hmacSha256(accessKeySecret, signStr);

    return {
        'Content-Type': 'application/json',
        'x-bili-accesskeyid': accessKeyId,
        'x-bili-content-md5': contentMd5,
        'x-bili-signature-method': 'HMAC-SHA256',
        'x-bili-signature-version': '1.0',
        'x-bili-signature-nonce': nonce,
        'x-bili-timestamp': timestamp,
        'Authorization': sig,
    };
}

function request(method, path, body, config) {
    return new Promise((resolve, reject) => {
        const bodyStr = JSON.stringify(body);
        const headers = buildHeaders(config.accessKeyId, config.accessKeySecret, bodyStr);

        const opts = {
            hostname: BASE_HOST,
            path: path,
            method: method,
            headers: headers,
        };

        const req = https.request(opts, (res) => {
            let data = '';
            res.on('data', c => data += c);
            res.on('end', () => {
                try { resolve(JSON.parse(data)); }
                catch (_) { reject(new Error('API 响应解析失败: ' + data.substring(0, 200))); }
            });
        });
        req.on('error', (e) => reject(new Error('API 请求失败: ' + e.message)));
        req.write(bodyStr);
        req.end();
    });
}

// 启动项目 → 获取 WebSocket 地址 + auth_body + game_id
async function start(config) {
    const res = await request('POST', '/v2/app/start', {
        code: config.code,
        app_id: config.appId,
    }, config);

    if (res.code !== 0) {
        const msg = res.message || res.msg || JSON.stringify(res);
        throw new Error('项目启动失败: ' + msg);
    }

    return {
        gameId: res.data.game_info.game_id,
        wssLinks: res.data.websocket_info.wss_link,
        authBody: res.data.websocket_info.auth_body,
    };
}

// HTTP 心跳（每 20-60s 调用一次，否则 WS 会被关闭）
async function heartbeat(config, gameId) {
    const res = await request('POST', '/v2/app/heartbeat', {
        game_id: gameId,
    }, config);
    // code 非 0 时只警告，不断开（可能是网络抖动）
    if (res.code !== 0) {
        console.warn('[OPL] 心跳异常: ' + (res.message || res.code));
    }
    return res;
}

// 结束项目
async function end(config, gameId) {
    const res = await request('POST', '/v2/app/end', {
        app_id: config.appId,
        game_id: gameId,
    }, config);
    return res;
}

module.exports = { start, heartbeat, end };
