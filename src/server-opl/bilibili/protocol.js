const zlib = require('zlib');

function encodePacket(op, body, version = 1) {
    const bodyStr = typeof body === 'string' ? body : JSON.stringify(body);
    const bodyBuf = Buffer.from(bodyStr, 'utf-8');
    const totalLen = 16 + bodyBuf.length;
    const header = Buffer.alloc(16);
    header.writeUInt32BE(totalLen, 0);
    header.writeUInt16BE(16, 4);
    header.writeUInt16BE(version, 6);
    header.writeUInt32BE(op, 8);
    header.writeUInt32BE(1, 12);
    return Buffer.concat([header, bodyBuf]);
}

function decodePackets(buffer) {
    if (!buffer || buffer.length < 16) return [];
    const messages = [];
    let offset = 0;
    while (offset + 16 <= buffer.length) {
        const totalLen  = buffer.readUInt32BE(offset);
        const headerLen = buffer.readUInt16BE(offset + 4);
        const version   = buffer.readUInt16BE(offset + 6);
        const op        = buffer.readUInt32BE(offset + 8);
        if (totalLen < 16 || headerLen !== 16) break;
        const bodyLen = totalLen - headerLen;
        const bodyBuf = buffer.subarray(offset + 16, offset + 16 + bodyLen);
        if (op === 5) {
            if (version === 3) {
                try { messages.push(...decodePackets(zlib.brotliDecompressSync(bodyBuf))); } catch (_) {}
            } else if (version === 2) {
                try { messages.push(...decodePackets(zlib.inflateSync(bodyBuf))); } catch (_) {}
            } else if (version === 0) {
                try { messages.push(JSON.parse(bodyBuf.toString('utf-8'))); } catch (_) {}
            } else {
                try { messages.push(JSON.parse(bodyBuf.toString('utf-8'))); } catch (_) {}
            }
        } else if (op === 3 || op === 8) {
            try {
                const json = JSON.parse(bodyBuf.toString('utf-8'));
                messages.push(op === 8 ? { _type: 'auth_reply', ...json } : json);
            } catch (_) {}
        } else {
            if (bodyBuf.length > 0) {
                try { messages.push(JSON.parse(bodyBuf.toString('utf-8'))); } catch (_) {}
            }
        }
        offset += totalLen;
        if (buffer.length - offset < 16) break;
    }
    return messages;
}

// 表情包正则：纯 [xxx] 格式（可能包含嵌套描述）
// 纯表情包：一个或多个连续的 [xxx]
const emoteRe = /^(\[.+]\s*)+$/;

function extractDanmaku(messages) {
    const list = [];
    for (const msg of messages) {
        if (!msg.cmd) continue;

        if (msg.cmd.startsWith('DANMU_MSG')) {
            try {
                const info = msg.info || [];
                if (!info[1]) continue;
                // 过滤纯表情包
                if (emoteRe.test(info[1].trim())) continue;
                const d = {
                    text: info[1],
                    mode: 1,
                    fontSize: (Array.isArray(info[0]) && info[0][2]) || 25,
                    color: (Array.isArray(info[0]) && info[0][3]) || 16777215,
                    username: getUserName(msg),
                    uid: getUserId(msg),
                    timestamp: Date.now(),
                };
                d.colorHex = '#' + d.color.toString(16).padStart(6, '0');
                list.push(d);
            } catch (_) {}
        }

        if (msg.cmd === 'SUPER_CHAT_MESSAGE') {
            try {
                const d = msg.data || msg;
                list.push({
                    text: d.message || d.content || '',
                    mode: 4, fontSize: 22,
                    color: parseInt(d.background_price_color || '16777215'),
                    colorHex: '#' + (d.background_price_color || 'ffffff'),
                    username: (d.user_info && d.user_info.uname) || '',
                    uid: (d.user_info && d.user_info.uid) || 0,
                    price: d.price || 0, isSC: true,
                    timestamp: Date.now(),
                });
            } catch (_) {}
        }

        if (msg.cmd === 'INTERACT_WORD_V2') {
            try {
                const d = msg.data || {};
                let uname = '';
                if (d.pb) {
                    const pb = Buffer.from(d.pb, 'base64');
                    const text = pb.toString('utf-8');
                    const match = text.match(/[一-鿿぀-ヿ가-힯\w]{2,12}/);
                    if (match) uname = match[0];
                }
                if (uname) {
                    list.push({
                        text: '进入了直播间',
                        mode: 1, fontSize: 22,
                        color: 0x00CCFF, colorHex: '#00ccff',
                        username: uname, uid: d.uid || 0,
                        timestamp: Date.now(), isEnter: true,
                    });
                }
            } catch (_) {}
        }

        if (msg.cmd === 'SEND_GIFT') {
            try {
                const d = msg.data || msg;
                list.push({
                    text: d.giftName + ' x' + (d.num || 1),
                    mode: 4, fontSize: 20,
                    color: 0xFF69B4, colorHex: '#ff69b4',
                    username: d.uname || '', uid: 0,
                    isGift: true,
                    timestamp: Date.now(),
                });
            } catch (_) {}
        }
    }
    return list;
}

function getUserName(msg) {
    try {
        const info = msg.info || [];
        if (Array.isArray(info[2]) && info[2][1]) return info[2][1];
        if (info[2] && info[2].base && info[2].base.name) return info[2].base.name;
        if (Array.isArray(info[3]) && info[3][1]) return info[3][1];
    } catch (_) {}
    return '未知用户';
}

function getUserId(msg) {
    try {
        const info = msg.info || [];
        if (Array.isArray(info[2]) && info[2][0]) return info[2][0];
        if (info[2] && info[2].uid) return info[2].uid;
    } catch (_) {}
    return 0;
}

module.exports = { encodePacket, decodePackets, extractDanmaku };
