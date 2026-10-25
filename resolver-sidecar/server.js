// Live Hub headless resolver sidecar.
// POST /resolve {"url","referer","userAgent","timeoutMs"} → {"url","headers","expiresAt"} | 404 {"error"}
// Loads the page in Chromium (Playwright) and returns the first HLS/DASH request it sees (also inside iframes).
// It runs no plugin code and only answers requests from the local network; put it in the VPN network (gluetun).
'use strict';

const http = require('node:http');
const { chromium } = require('playwright');

const PORT = Number(process.env.PORT || 3000);
const MAX_CONCURRENCY = Math.max(1, Number(process.env.MAX_CONCURRENCY || 2));
const MAX_TIMEOUT = 45000;
const MANIFEST = /\.(m3u8|mpd)(\?|$)/i;

let browser;
let running = 0;

async function getBrowser() {
    if (!browser || !browser.isConnected()) {
        browser = await chromium.launch({ headless: true, args: ['--no-sandbox', '--autoplay-policy=no-user-gesture-required'] });
    }
    return browser;
}

function json(res, status, body) {
    res.writeHead(status, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify(body));
}

function expiry(url) {
    const m = /[?&~](?:exp|expires|e)=(\d{10,13})/i.exec(url);
    if (!m) { return null; }
    const v = Number(m[1]);
    return v > 1e11 ? Math.floor(v / 1000) : v;
}

async function resolve({ url, referer, userAgent, timeoutMs }) {
    const target = new URL(url);
    if (target.protocol !== 'http:' && target.protocol !== 'https:') {
        throw new Error('Only http(s) URLs are allowed');
    }
    const timeout = Math.min(Number(timeoutMs) || 20000, MAX_TIMEOUT);
    const context = await (await getBrowser()).newContext({
        userAgent: userAgent || undefined,
        extraHTTPHeaders: referer ? { Referer: referer } : {},
        ignoreHTTPSErrors: false,
    });
    try {
        const page = await context.newPage();
        const found = new Promise(resolvePromise => {
            context.on('request', request => {
                const u = request.url();
                if (MANIFEST.test(u)) {
                    const headers = request.headers();
                    const keep = {};
                    for (const k of ['referer', 'origin', 'user-agent', 'cookie', 'authorization']) {
                        if (headers[k]) { keep[k.replace(/(^|-)\w/g, c => c.toUpperCase())] = headers[k]; }
                    }
                    resolvePromise({ url: u, headers: keep, expiresAt: expiry(u) });
                }
            });
        });
        page.goto(url, { waitUntil: 'domcontentloaded', timeout }).catch(() => { /* the manifest may already be found */ });
        // Many players start on interaction: click in the middle of the page and in iframes.
        setTimeout(() => { page.mouse.click(400, 300).catch(() => {}); }, 2500);
        const timer = new Promise(r => setTimeout(() => r(null), timeout));
        return await Promise.race([found, timer]);
    } finally {
        await context.close().catch(() => {});
    }
}

const server = http.createServer(async (req, res) => {
    if (req.method === 'GET' && req.url === '/health') {
        return json(res, 200, { ok: true, running });
    }
    if (req.method !== 'POST' || req.url !== '/resolve') {
        return json(res, 404, { error: 'not found' });
    }
    if (running >= MAX_CONCURRENCY) {
        return json(res, 429, { error: 'busy' });
    }
    let body = '';
    req.on('data', chunk => { body += chunk; if (body.length > 65536) { req.destroy(); } });
    req.on('end', async () => {
        running++;
        try {
            const result = await resolve(JSON.parse(body || '{}'));
            if (!result) { return json(res, 404, { error: 'no stream found' }); }
            return json(res, 200, result);
        } catch (e) {
            return json(res, 400, { error: String(e && e.message || e) });
        } finally {
            running--;
        }
    });
});

server.listen(PORT, () => console.log(`livehub-resolver listening on :${PORT}`));
process.on('SIGTERM', async () => { server.close(); if (browser) { await browser.close(); } process.exit(0); });
