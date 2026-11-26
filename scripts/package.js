// Copyright (c) 2026 猫猫越 <liu2128783468@outlook.com> — Licensed under MIT
// 打包发布：生成 dist/BiliOverlay-WE-v<版本>.zip 和 dist/BiliOverlay-OPL-v<版本>-preview.zip
//
// 用法：npm run package   （仅限 Windows：调用 build.bat 编译浮窗，用系统自带的 tar.exe 压缩）
//
// 每个压缩包解压后就能直接用：双击 exe，浮窗会用包里自带的 nodejs\node.exe 在后台启动服务端。
// 只按白名单复制文件，本机设置（we-settings.json / opl-settings.json，含 Cookie / 密钥）不会进包，
// 打包完还会再扫描一遍，发现疑似凭证就中止。
//
// 可选环境变量：
//   BUNDLE_NODE_VERSION  内置的 Node.js 版本（默认见 NODE_VERSION）
//   NODE_MIRROR          Node.js 下载源（默认先试 nodejs.org，失败再试 npmmirror）
'use strict';

const fs = require('fs');
const path = require('path');
const https = require('https');
const crypto = require('crypto');
const { execFileSync } = require('child_process');

const ROOT = path.resolve(__dirname, '..');
const pkg = JSON.parse(fs.readFileSync(path.join(ROOT, 'package.json'), 'utf8'));
const VERSION = 'v' + pkg.version;
const NODE_VERSION = process.env.BUNDLE_NODE_VERSION || 'v24.16.0';
const NODE_DIST = `node-${NODE_VERSION}-win-x64`;
const NODE_MIRRORS = process.env.NODE_MIRROR
    ? [process.env.NODE_MIRROR.replace(/\/+$/, '')]
    : ['https://nodejs.org/dist', 'https://npmmirror.com/mirrors/node'];
const CACHE_DIR = path.join(ROOT, '.cache');
const DIST_DIR = path.join(ROOT, 'dist');
const STAGE_DIR = path.join(DIST_DIR, 'stage');
const TAR = path.join(process.env.SystemRoot || 'C:\\Windows', 'System32', 'tar.exe');   // 不用 Git 自带的 GNU tar，它不支持 zip

const VARIANTS = [
    {
        id: 'WE',
        name: `BiliOverlay-WE-${VERSION}`,
        exe: 'DanmakuOverlay-WE.exe',
        files: [
            'src/config.js',
            'src/shared/protocol.js',
            'src/server-we/index.js',
            'src/server-we/bilibili/api.js',
        ],
        readme: weReadme,
    },
    {
        id: 'OPL',
        name: `BiliOverlay-OPL-${VERSION}-preview`,
        exe: 'DanmakuOverlay-OPL.exe',
        files: [
            'src/config.js',
            'src/shared/protocol.js',
            'src/server-opl/opl-service.js',
            'src/server-opl/bilibili/open-platform.js',
        ],
        readme: oplReadme,
    },
];

// ── 工具函数 ───────────────────────────────────────────────────
function log(msg) { console.log('[package] ' + msg); }

function fail(msg) {
    console.error('[package] ✗ ' + msg);
    process.exit(1);
}

function copy(src, dest) {
    fs.mkdirSync(path.dirname(dest), { recursive: true });
    fs.cpSync(src, dest, { recursive: true });
}

function sha256(file) {
    return crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
}

function download(url, dest, redirects = 5) {
    return new Promise((resolve, reject) => {
        const req = https.get(url, { headers: { 'User-Agent': 'BiliOverlay-packager' } }, (res) => {
            if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location && redirects > 0) {
                res.resume();
                resolve(download(new URL(res.headers.location, url).toString(), dest, redirects - 1));
                return;
            }
            if (res.statusCode !== 200) {
                res.resume();
                reject(new Error('HTTP ' + res.statusCode + ' ' + url));
                return;
            }
            const tmp = dest + '.part';
            const out = fs.createWriteStream(tmp);
            res.pipe(out);
            out.on('finish', () => out.close(() => { fs.renameSync(tmp, dest); resolve(); }));
            out.on('error', reject);
            res.on('error', reject);
        });
        req.setTimeout(60000, () => req.destroy(new Error('下载超时 ' + url)));
        req.on('error', reject);
    });
}

function projectUrl() {
    try {
        const url = execFileSync('git', ['remote', 'get-url', 'origin'], { cwd: ROOT, encoding: 'utf8' }).trim();
        const m = /github\.com[/:]([^/]+\/[^/]+?)(?:\.git)?$/.exec(url);
        if (m) return 'https://github.com/' + m[1];
    } catch (_) {}
    return String((pkg.repository && pkg.repository.url) || '').replace(/^git\+/, '').replace(/\.git$/, '');
}

// 文本文件写成 UTF-8 BOM + CRLF，用记事本打开也不会乱码
function writeText(file, text) {
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, '\uFEFF' + text.replace(/\r?\n/g, '\r\n'), 'utf8');
}

// ── 第 1 步：编译浮窗 ─────────────────────────────────────────
function buildOverlays() {
    log('编译浮窗 ...');
    execFileSync('cmd.exe', ['/d', '/c', 'build.bat', '--no-pause'], { cwd: ROOT, stdio: 'inherit' });
    for (const v of VARIANTS) {
        if (!fs.existsSync(path.join(ROOT, v.exe))) fail('编译后找不到 ' + v.exe);
    }
}

// ── 第 2 步：准备内置 Node.js（下载官方压缩包并校验 SHA256）──────
async function prepareNode() {
    const outDir = path.join(CACHE_DIR, NODE_DIST);
    const nodeExe = path.join(outDir, 'node.exe');
    const license = path.join(outDir, 'LICENSE');
    if (fs.existsSync(nodeExe) && fs.existsSync(license)) {
        log('使用缓存的 Node.js ' + NODE_VERSION);
        return { nodeExe, license };
    }
    fs.mkdirSync(CACHE_DIR, { recursive: true });
    const zipName = NODE_DIST + '.zip';
    const zipPath = path.join(CACHE_DIR, zipName);
    const sumsPath = path.join(CACHE_DIR, `SHASUMS256-${NODE_VERSION}.txt`);

    let lastErr;
    for (const mirror of NODE_MIRRORS) {
        try {
            log('下载 Node.js ' + NODE_VERSION + '（' + mirror + '）...');
            await download(`${mirror}/${NODE_VERSION}/SHASUMS256.txt`, sumsPath);
            if (!fs.existsSync(zipPath)) await download(`${mirror}/${NODE_VERSION}/${zipName}`, zipPath);
            lastErr = null;
            break;
        } catch (e) {
            lastErr = e;
            log('下载失败：' + e.message);
        }
    }
    if (lastErr) fail('Node.js 下载失败，可以设置 NODE_MIRROR 换一个下载源后重试');

    const line = fs.readFileSync(sumsPath, 'utf8').split('\n').find(l => l.trim().endsWith('  ' + zipName));
    const expected = line && line.trim().split(/\s+/)[0];
    const actual = sha256(zipPath);
    if (!expected || expected !== actual) {
        fs.rmSync(zipPath, { force: true });
        fail(`Node.js 压缩包校验失败（期望 ${expected || '未找到'}，实际 ${actual}），已删除，请重试`);
    }
    log('Node.js 压缩包 SHA256 校验通过');

    execFileSync(TAR, ['-x', '-f', zipPath, '-C', CACHE_DIR, `${NODE_DIST}/node.exe`, `${NODE_DIST}/LICENSE`]);
    if (!fs.existsSync(nodeExe) || !fs.existsSync(license)) fail('从压缩包里解出 node.exe / LICENSE 失败');
    return { nodeExe, license };
}

// ── 第 3 步：按白名单组装目录 ──────────────────────────────────
// 目录结构和源码仓库一致，浮窗会自动找到 src\server-*\ 下的脚本和 nodejs\node.exe
function stage(v, node, homepage) {
    const dir = path.join(STAGE_DIR, v.name);
    copy(path.join(ROOT, v.exe), path.join(dir, v.exe));
    for (const f of v.files) copy(path.join(ROOT, f), path.join(dir, f));
    copy(path.join(ROOT, 'node_modules', 'ws'), path.join(dir, 'node_modules', 'ws'));
    copy(node.nodeExe, path.join(dir, 'nodejs', 'node.exe'));
    copy(node.license, path.join(dir, 'nodejs', 'LICENSE'));
    copy(path.join(ROOT, 'LICENSE'), path.join(dir, 'LICENSE'));
    writeText(path.join(dir, 'README.txt'), v.readme(homepage));
    return dir;
}

// ── 第 4 步：检查（不含凭证、服务端依赖齐全）──────────────────
function walk(dir, out = []) {
    for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
        const p = path.join(dir, e.name);
        if (e.isDirectory()) walk(p, out); else out.push(p);
    }
    return out;
}

function verify(dir, v) {
    const files = walk(dir);
    for (const f of files) {
        const rel = path.relative(dir, f);
        if (/settings\.json$/i.test(rel)) fail(`${v.name} 里出现了设置文件 ${rel}，已中止`);
        if (/\.(js|json|txt|md)$/i.test(rel)) {
            const t = fs.readFileSync(f, 'utf8');
            if (/SESSDATA=[^;'"\s]{8,}|bili_jct=[0-9a-f]{16,}/i.test(t)) fail(`${v.name} 的 ${rel} 里疑似含有 Cookie，已中止`);
        }
    }
    // 用包里自带的 node 检查语法和 ws 能否被找到（不启动服务端）
    const nodeExe = path.join(dir, 'nodejs', 'node.exe');
    for (const f of v.files) execFileSync(nodeExe, ['--check', f], { cwd: dir });
    const entryDir = path.join(dir, path.dirname(v.files[v.files.length - 2]));
    execFileSync(nodeExe, ['-e', `require.resolve('ws', { paths: [${JSON.stringify(entryDir)}] })`], { cwd: dir });
    log(`${v.name}：${files.length} 个文件，检查通过`);
}

// ── 第 5 步：压缩 ────────────────────────────────────────────
function zip(v) {
    const out = path.join(DIST_DIR, v.name + '.zip');
    fs.rmSync(out, { force: true });
    execFileSync(TAR, ['-a', '-c', '-f', out, '-C', STAGE_DIR, v.name]);
    return out;
}

async function main() {
    if (process.platform !== 'win32') fail('只能在 Windows 上打包');
    if (!fs.existsSync(TAR)) fail('找不到系统自带的 tar.exe：' + TAR);
    if (!fs.existsSync(path.join(ROOT, 'node_modules', 'ws'))) fail('缺少依赖 ws，请先运行 npm install');

    buildOverlays();
    const node = await prepareNode();
    const homepage = projectUrl();

    fs.rmSync(STAGE_DIR, { recursive: true, force: true });
    const sums = [];
    for (const v of VARIANTS) {
        const dir = stage(v, node, homepage);
        verify(dir, v);
        const out = zip(v);
        const size = (fs.statSync(out).size / 1024 / 1024).toFixed(1);
        sums.push(`${sha256(out)}  ${path.basename(out)}`);
        log(`✓ ${path.relative(ROOT, out)}（${size} MB）`);
    }
    fs.writeFileSync(path.join(DIST_DIR, 'SHA256SUMS.txt'), sums.join('\n') + '\n');
    log('完成，SHA256 见 dist/SHA256SUMS.txt');
}

// ── 压缩包里的说明文件 ─────────────────────────────────────────
function commonFooter(homepage) {
    return `【首次运行提示「Windows 已保护你的电脑」】
程序没有数字签名，点「更多信息」→「仍要运行」即可。

【其他】
- 游戏建议使用「无边框窗口」模式
- WE 版和 OPL 版不要同时运行
- 程序卡住：任务管理器里结束 node.exe 和 DanmakuOverlay 开头的进程

项目主页：${homepage}
本程序以 MIT 许可发布，版权声明见 LICENSE
内置 Node.js ${NODE_VERSION}，许可见 nodejs\\LICENSE
`;
}

function weReadme(homepage) {
    return `BiliOverlay WE 版 ${VERSION} — B站直播弹幕透明浮窗

【使用方法】
1. 先把整个文件夹解压出来，不要在压缩包里直接运行
2. 双击 DanmakuOverlay-WE.exe
3. 点屏幕右下角的齿轮 → 输入直播间号 → 应用
   （会记住，下次打开自动连接）

【常用操作】
- Ctrl+Shift+F12：锁定 / 解锁（玩游戏时请锁定，鼠标可以穿透浮窗）
- Ctrl+Shift+Enter：呼出输入框直接发弹幕，Enter 发送，Esc 取消
- 退出：齿轮面板最下方的「退出」按钮

【用户名被打码 / 想发弹幕】
需要在齿轮面板里填写 B站登录 Cookie：
1. 用浏览器登录 B站，打开任意直播间
2. 按 F12 → 选「网络(Network)」→ 按 F5 刷新
3. 点列表里任意一个请求，在「请求标头」里找到 Cookie，复制整段
4. 粘贴到齿轮面板的 Cookie 输入框 → 保存，看到「✓ 已登录」即可

注意：Cookie 相当于你的 B站登录凭证，保存在 src\\we-settings.json，
不要把这个文件或整个文件夹发给别人。

${commonFooter(homepage)}`;
}

function oplReadme(homepage) {
    return `BiliOverlay OPL 版 ${VERSION}（预览版）— B站直播弹幕透明浮窗

⚠ 预览版：本版本还没有用真实的开放平台凭证测试过，可能无法正常连接。
  遇到问题欢迎到项目主页反馈。

OPL 版通过 B站直播开放平台接收弹幕，只能显示你自己直播间的弹幕，
需要先注册开放平台开发者：https://open-live.bilibili.com/

【使用方法】
1. 先把整个文件夹解压出来，不要在压缩包里直接运行
2. 双击 DanmakuOverlay-OPL.exe
3. 点屏幕右下角的齿轮 → 填写 accessKeyId、accessKeySecret、
   项目 ID（appId）、主播身份码（code）→ 保存并连接

【常用操作】
- Ctrl+Shift+F12：锁定 / 解锁（玩游戏时请锁定，鼠标可以穿透浮窗）
- 退出：齿轮面板最下方的「退出」按钮

注意：凭证以明文保存在 src\\opl-settings.json，不要把这个文件或整个文件夹发给别人。

${commonFooter(homepage)}`;
}

main().catch((e) => fail(e && e.stack || String(e)));
