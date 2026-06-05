// ============================================
// B站 REST API 模块
// 负责：获取房间信息、WBI签名、获取WebSocket连接token
// ============================================

const crypto = require('crypto');

// B站 WBI 签名固定的置换表（来自B站前端，长期稳定）
const MIXIN_KEY_ENC_TAB = [
    46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35,
    27, 43, 5, 49, 33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13,
    37, 48, 7, 16, 24, 55, 40, 61, 26, 17, 0, 1, 60, 51, 30, 4,
    22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11, 36, 20, 52, 44, 34
];

// 缓存WBI密钥（10分钟过期）
let cachedWbiKeys = null;
let wbiKeysExpireAt = 0;

// ---------- HTTP 请求辅助 ----------

/**
 * 发送HTTP GET请求
 * @param {string} url
 * @param {object} headers
 * @returns {Promise<object>} JSON响应体
 */
async function httpGet(url, headers = {}) {
    const isHttps = url.startsWith('https:');
    const http = isHttps ? require('https') : require('http');

    return new Promise((resolve, reject) => {
        const req = http.get(url, { headers }, (res) => {
            let data = '';
            res.on('data', chunk => data += chunk);
            res.on('end', () => {
                try {
                    resolve(JSON.parse(data));
                } catch {
                    // 非JSON响应，返回原始文本
                    resolve({ _raw: data, _status: res.statusCode });
                }
            });
        });
        req.on('error', reject);
        req.setTimeout(10000, () => { req.destroy(); reject(new Error('请求超时')); });
    });
}

// ---------- 房间信息 ----------

/**
 * 把短房间号转成真实房间号
 * @param {number|string} roomId - 可能为短号的房间ID
 * @returns {Promise<number>} 真实房间号
 */
async function getRealRoomId(roomId) {
    const url = `https://api.live.bilibili.com/room/v1/Room/get_info?room_id=${roomId}`;
    const resp = await httpGet(url);

    if (resp.code === 0 && resp.data && resp.data.room_id) {
        return resp.data.room_id;
    }

    // 如果API失败，可能已经是真实ID，直接返回
    return Number(roomId);
}

// ---------- WBI 签名 ----------

/**
 * 获取WBI签名所需的密钥
 * 从B站nav接口提取img_key和sub_key
 */
async function fetchWbiKeys() {
    const now = Date.now();
    if (cachedWbiKeys && now < wbiKeysExpireAt) {
        return cachedWbiKeys;
    }

    const url = 'https://api.bilibili.com/x/web-interface/nav';
    const resp = await httpGet(url, {
        'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36',
        'Referer': 'https://www.bilibili.com/',
    });

    if (resp.code === 0 && resp.data && resp.data.wbi_img) {
        const imgUrl = resp.data.wbi_img.img_url || '';
        const subUrl = resp.data.wbi_img.sub_url || '';

        // 从URL中取文件名（去掉扩展名）
        const imgKey = imgUrl.split('/').pop().split('.')[0];
        const subKey = subUrl.split('/').pop().split('.')[0];

        cachedWbiKeys = { imgKey, subKey };
        wbiKeysExpireAt = now + 10 * 60 * 1000; // 10分钟缓存
        return cachedWbiKeys;
    }

    // 如果获取失败，用备用密钥（一般也能用）
    return {
        imgKey: '7cd084941338484aae1ad9425b84077c',
        subKey: '4932caff0ff746eab6f01bf08b70ac45'
    };
}

/**
 * 生成WBI签名密钥
 * @param {string} orig - imgKey + subKey 拼接的原始字符串
 * @returns {string} 置换后的32位密钥
 */
function getMixinKey(orig) {
    let result = '';
    for (const idx of MIXIN_KEY_ENC_TAB) {
        if (idx < orig.length) {
            result += orig[idx];
        }
    }
    // 截取前32位
    return result.substring(0, 32);
}

/**
 * 对请求参数进行WBI签名
 * @param {object} params - 请求参数（不含w_rid和wts）
 * @param {object} keys - WBI密钥 {imgKey, subKey}
 * @returns {object} 包含签名后的完整参数
 */
function signParams(params, keys) {
    const orig = keys.imgKey + keys.subKey;
    const mixinKey = getMixinKey(orig);

    // 添加时间戳
    const signed = { ...params, wts: Math.floor(Date.now() / 1000) };

    // 按key字母顺序排序
    const sorted = Object.keys(signed)
        .sort()
        .map(k => `${k}=${encodeURIComponent(signed[k])}`)
        .join('&');

    // MD5签名
    const signStr = sorted + mixinKey;
    const wRid = crypto.createHash('md5').update(signStr).digest('hex');

    return { ...signed, w_rid: wRid };
}

// ---------- WebSocket 连接信息 ----------

/**
 * 获取直播间WebSocket连接所需的token和服务器地址
 * @param {number} roomId - 真实房间号
 * @returns {Promise<{token: string, hosts: Array<{host: string, port: number, wss_port: number}>}>}
 */
async function getDanmuInfo(roomId) {
    const keys = await fetchWbiKeys();
    const params = signParams({ id: roomId }, keys);

    const query = Object.entries(params)
        .map(([k, v]) => `${k}=${v}`)
        .join('&');

    const url = `https://api.live.bilibili.com/xlive/web-room/v1/index/getDanmuInfo?${query}`;
    const resp = await httpGet(url, {
        'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36',
        'Referer': `https://live.bilibili.com/${roomId}`,
    });

    if (resp.code === 0 && resp.data) {
        return {
            token: resp.data.token || '',
            hosts: (resp.data.host_list || []).map(h => ({
                host: h.host,
                port: h.port,
                wss_port: h.wss_port,
            })),
        };
    }

    throw new Error(`获取弹幕信息失败: code=${resp.code} msg=${resp.message || resp._raw || '未知错误'}`);
}

module.exports = { getRealRoomId, getDanmuInfo };
