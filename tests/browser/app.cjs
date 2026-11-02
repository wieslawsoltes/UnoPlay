// Exercises the actual published Uno application, including its GitHub Pages mount point.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const http = require("node:http");
const { execFileSync } = require("node:child_process");
const path = require("node:path");
const { chromium } = require("playwright");

const root = path.resolve(process.argv[2] || "artifacts/site");
const mime = { ".html": "text/html", ".js": "text/javascript", ".mjs": "text/javascript",
    ".css": "text/css", ".wasm": "application/wasm", ".json": "application/json",
    ".mp3": "audio/mpeg", ".mp4": "video/mp4", ".svg": "image/svg+xml", ".png": "image/png" };
const server = http.createServer((req, res) => {
    const route = decodeURIComponent(new URL(req.url, "http://localhost").pathname);
    if (!route.startsWith("/UnoPlay/")) { res.writeHead(404); return res.end(); }
    const file = path.resolve(root, route.slice("/UnoPlay/".length) || "index.html");
    if (!file.startsWith(root + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) {
        res.writeHead(404); return res.end();
    }
    res.setHeader("Content-Type", mime[path.extname(file)] || "application/octet-stream");
    const size = fs.statSync(file).size;
    res.setHeader("Accept-Ranges", "bytes");
    const bytes = /^bytes=(\d+)-(\d*)$/.exec(req.headers.range || "");
    if (bytes) {
        const start = Number(bytes[1]), end = bytes[2] ? Math.min(Number(bytes[2]), size - 1) : size - 1;
        if (start > end || start >= size) { res.writeHead(416, {"Content-Range": `bytes */${size}`}); return res.end(); }
        res.writeHead(206, {"Content-Range": `bytes ${start}-${end}/${size}`, "Content-Length": end - start + 1});
        return fs.createReadStream(file, { start, end }).pipe(res);
    }
    res.setHeader("Content-Length", size);
    fs.createReadStream(file).pipe(res);
});

(async () => {
    await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
    let browser;
    try {
        browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH, args: ["--disable-dev-shm-usage"] });
        const page = await browser.newPage({ viewport: { width: 1060, height: 820 } });
        const errors = [];
        // Exercise the HTML file chooser used by browsers without the File System Access API.
        await page.addInitScript(() => { delete window.showOpenFilePicker; });
        page.on("pageerror", e => errors.push(e.message));
        const url = process.env.UNOPLAY_URL || `http://127.0.0.1:${server.address().port}/UnoPlay/`;
        const ready = () => page.waitForFunction(() => {
            const c = document.querySelector('[xamltype="UnoPlay.ClassicUI.ClassicPlayerView"]');
            return c && c.clientHeight > 0 && !document.querySelector(".uno-loader");
        }, null, { timeout: 90000 });
        await page.goto(url);
        // Do not resize to trigger layout. The first frame must appear unassisted.
        await ready();
        const panels = page.locator('[xamltype="UnoPlay.ClassicUI.ClassicSurface"]');
        async function clickMain(x, y) {
            const b = await panels.first().boundingBox();
            assert.ok(b && b.width >= 275, "main player bounds");
            const k = b.width / 275;
            await page.mouse.click(b.x + x * k, b.y + y * k);
        }
        await clickMain(50, 96); // play the bundled tone through the real transport button
        await page.waitForFunction(() => {
            const v = document.querySelector("video");
            return v && !v.paused && v.currentTime > .15 && v.currentSrc.endsWith("/UnoPlay/media/first-light.mp3");
        });
        if (process.env.UNOPLAY_SCREENSHOT) {
            await page.waitForFunction(() => document.querySelector("video").currentTime > 1.5 &&
                [...document.querySelectorAll("img.uno-htmlimage")].every(i => !i.src || i.complete));
            await page.mouse.move(8, 8);
            await page.screenshot({path: process.env.UNOPLAY_SCREENSHOT});
        }
        console.log("First frame and playback passed");
        await clickMain(70, 96);
        await page.waitForFunction(() => document.querySelector("video")?.paused);
        console.log("Pause passed");
        await clickMain(160, 77);
        await page.waitForFunction(() => document.querySelector("video").currentTime > 3);
        console.log("Checking equalizer panel");
        await clickMain(230, 63);
        await panels.nth(1).waitFor({ state: "hidden" });
        await page.waitForFunction(() => Object.values(localStorage).some(value =>
            value.includes('"Visible":[true,false,')), null, { timeout: 10000 });
        await page.reload();
        await ready();
        await panels.nth(1).waitFor({ state: "hidden" });
        await clickMain(230, 63);
        await panels.nth(1).waitFor({ state: "visible" });
        await clickMain(258, 7); // title-bar roll-up
        await page.waitForFunction(() => {
            const panel = document.querySelector('[xamltype="UnoPlay.ClassicUI.ClassicSurface"]');
            return Math.abs(panel.clientHeight - 14 * panel.clientWidth / 275) < 1;
        });
        await clickMain(258, 7);
        const fixture = execFileSync("python3", [path.join(__dirname, "skin_fixture.py")]);
        const options = page.getByRole("button", {name: "Options", exact: true});
        await options.click();
        const choice = page.waitForEvent("filechooser");
        await page.getByText(/Load classic skin/).first().click();
        await (await choice).setFiles({name: "Test.wsz", mimeType: "application/zip", buffer: fixture});
        const skinVisible = () => page.waitForFunction(() => {
            const sprite = document.querySelector('[xamltype="UnoPlay.ClassicUI.ClassicSurface"] img');
            if (!sprite || sprite.naturalWidth !== 275) return false;
            const canvas = document.createElement("canvas");
            canvas.width = canvas.height = 1;
            const ctx = canvas.getContext("2d");
            ctx.drawImage(sprite, 0, 0);
            const [r,g,b] = ctx.getImageData(0,0,1,1).data;
            return r === 0xdd && g === 0x88 && b === 0x3a;
        });
        await skinVisible();
        await page.waitForFunction(async expected => (await globalThis.UnoPlayStorage.load()).length === expected, fixture.length);
        await page.reload();
        await ready();
        await skinVisible();
        assert.deepEqual(errors, []);
        console.log("Published app passed cold startup, MP3 playback, pause, seek, panel toggles, saved settings, windowshade, skin upload, pixel color and restored skin.");
    } finally {
        await browser?.close();
        await new Promise(resolve => server.close(resolve));
    }
})().catch(e => { console.error(e); process.exitCode = 1; });
