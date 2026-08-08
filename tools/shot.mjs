// Minimal CDP driver — screenshots a URL and reads text out of the page, with a REAL clock.
//
// Chromium's --screenshot flag needs --virtual-time-budget to wait for an async page, and virtual
// time does not advance during synchronous execution, so performance.now() deltas inside the WASM
// render all come back as 0.000 ms. Driving the browser over the DevTools protocol instead keeps
// wall-clock time real, which is the whole point when the number being measured IS the time.
//
// Usage: node tools/shot.mjs <url> <out.png> [waitMs] [selectorToRead] [jsToRunFirst]
// jsToRunFirst lets a check drive the page (pick a font, tick a box) instead of only loading a
// URL — the page has controls whose behaviour is not reachable through query params.
// No dependencies: Node 22+ ships a global WebSocket.

const [, , url, out, waitMs = '9000', readSel = '', evalFirst = ''] = process.argv;
if (!url || !out) {
    console.error('usage: node tools/shot.mjs <url> <out.png> [waitMs] [selector]');
    process.exit(2);
}

const PORT = 9223;
const CHROME = process.env.CHROME_PATH
    ?? `${process.env.LOCALAPPDATA}\\ms-playwright\\chromium-1228\\chrome-win64\\chrome.exe`;

const { spawn } = await import('node:child_process');
const chrome = spawn(CHROME, [
    '--headless=new', '--disable-gpu', '--no-sandbox', '--hide-scrollbars',
    `--remote-debugging-port=${PORT}`, '--window-size=1400,1000',
    'about:blank',
], { stdio: 'ignore' });

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// Wait for the debugging endpoint to come up.
let wsUrl = null;
for (let i = 0; i < 60 && !wsUrl; i++) {
    await sleep(250);
    try {
        const tab = await (await fetch(`http://127.0.0.1:${PORT}/json/new?about:blank`, { method: 'PUT' })).json();
        wsUrl = tab.webSocketDebuggerUrl;
    } catch { /* not listening yet */ }
}
if (!wsUrl) { chrome.kill(); throw new Error('Chrome DevTools endpoint never came up'); }

const ws = new WebSocket(wsUrl);
await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });

let nextId = 1;
const pending = new Map();
ws.onmessage = (ev) => {
    const msg = JSON.parse(ev.data);
    if (msg.id && pending.has(msg.id)) { pending.get(msg.id)(msg.result); pending.delete(msg.id); }
};
const send = (method, params = {}) => new Promise((res) => {
    const id = nextId++;
    pending.set(id, res);
    ws.send(JSON.stringify({ id, method, params }));
});

await send('Page.enable');
await send('Runtime.enable');
await send('Page.navigate', { url });

// Blazor boots, fetches a font, then renders — no single event covers all of it, so wait it out.
await sleep(Number(waitMs));

if (evalFirst) {
    await send('Runtime.evaluate', { expression: evalFirst, awaitPromise: true, returnByValue: true });
    await sleep(1500);   // let Blazor re-render and repaint
}

if (readSel) {
    const r = await send('Runtime.evaluate', {
        expression: `(document.querySelector(${JSON.stringify(readSel)})?.innerText ?? '<no match>')`,
        returnByValue: true,
    });
    console.log(r.result?.value ?? '<no result>');
}

const shot = await send('Page.captureScreenshot', { format: 'png' });
const { writeFileSync } = await import('node:fs');
writeFileSync(out, Buffer.from(shot.data, 'base64'));
console.log(`wrote ${out}`);

ws.close();
chrome.kill();
process.exit(0);
