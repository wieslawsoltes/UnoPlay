// Run with node tests/media/playback.cjs after installing playwright + Chromium.
// ffmpeg generates the original test recordings locally.
const assert = require("node:assert/strict");
const {spawnSync} = require("node:child_process");
const {createServer} = require("node:http");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const {chromium} = require("playwright");
const fixtures = fs.mkdtempSync(path.join(os.tmpdir(), "unoplay-media-"));
const lib = path.resolve(__dirname, "../../src/UnoPlay.Media");
function encode(...args) {
    const result = spawnSync("ffmpeg", ["-hide_banner", "-loglevel", "error", "-y", ...args], {cwd: fixtures, encoding:"utf8"});
    assert.equal(result.status, 0, result.stderr);
}
function serve() {
    const server = createServer((req, res) => {
        const name = new URL(req.url, "http://localhost").pathname;
        if (name === "/") return res.end('<!doctype html><script src="/unoplay-media.js"></script>');
        const file = name === "/unoplay-media.js" ? path.join(lib,"WasmScripts",name.slice(1))
            : name === "/media/hls-1.7.4.min.mjs" ? path.join(lib,"BrowserVendor",path.basename(name))
            : path.join(fixtures,path.basename(name));
        if (!fs.existsSync(file)) { res.writeHead(404); return res.end(); }
        const content = fs.readFileSync(file);
        const ext = path.extname(file);
        const types = {".mjs":"text/javascript",".js":"text/javascript",".mp3":"audio/mpeg",".mp4":"video/mp4",".m3u8":"application/vnd.apple.mpegurl",".ts":"video/mp2t"};
        const headers = {"Content-Type":types[ext],"Accept-Ranges":"bytes"};
        const range = /^bytes=(\d+)-(\d*)$/.exec(req.headers.range || "");
        if (range) {
            const start = Number(range[1]);
            const end = range[2] ? Math.min(Number(range[2]), content.length - 1) : content.length - 1;
            res.writeHead(206,{...headers,"Content-Length":end-start+1,"Content-Range":`bytes ${start}-${end}/${content.length}`});
            return res.end(content.subarray(start,end+1));
        }
        res.writeHead(200,{...headers,"Content-Length":content.length});
        res.end(content);
    });
    return new Promise(resolve => server.listen(0,"127.0.0.1",()=>resolve(server)));
}

(async () => {
    const tone = ["-f","lavfi","-i","sine=frequency=1000:duration=4","-af","volume=0.19"];
    encode(...tone,"-codec:a","libmp3lame","tone.mp3");
    encode("-f","lavfi","-i","testsrc2=size=96x64:rate=10:duration=2","-c:v","libx264","-pix_fmt","yuv420p","-movflags","+faststart","clip.mp4");
    encode(...tone,"-c:a","aac","-hls_time","1","-hls_list_size","0","index.m3u8");
    const main = await serve(), radio = await serve();
    let browser;
    try {
        browser = await chromium.launch({executablePath:process.env.CHROMIUM_PATH, args:["--no-sandbox","--autoplay-policy=no-user-gesture-required"]});
        const page = await browser.newPage();
        await page.goto(`http://127.0.0.1:${main.address().port}/`);
        await page.evaluate(() => {
            window.signals = [];
            window.systemActions = new Map();
            const register = MediaSession.prototype.setActionHandler;
            MediaSession.prototype.setActionHandler = function(action, handler) {
                register.call(this, action, handler);
                if (handler) systemActions.set(action, handler);
                else systemActions.delete(action);
            };
            window.id = "media-test";
            const container = document.createElement("div");
            container.id = id;
            document.body.append(container);
            UnoPlayMedia.create(id,(...arg) => signals.push(arg));
            UnoPlayMedia.attach(container);
            container.style.width = "275px";
            container.style.height = "200px";
            window.start = async url => { UnoPlayMedia.open(id,url); await UnoPlayMedia.play(id); };
            window.peak = () => {
                const samples = new Uint8Array(75);
                UnoPlayMedia.sample(id,samples,false);
                return Math.max(...samples);
            };
        });
        await page.evaluate(async () => {
            UnoPlayMedia.metadata(id, "Test tone", "UnoPlay", "Transport checks");
            await start("tone.mp3");
        });
        await page.waitForFunction(() => UnoPlayMedia.state(id,"playing") && UnoPlayMedia.state(id,"position") > .3 && peak() > 120);
        assert.equal(await page.evaluate(() => navigator.mediaSession.metadata.title), "Test tone");
        assert.equal(await page.evaluate(() => navigator.mediaSession.playbackState), "playing");
        await page.evaluate(() => systemActions.get("nexttrack")({action:"nexttrack"}));
        assert.ok(await page.evaluate(() => signals.some(([kind,command]) => kind === "command" && command === "nexttrack")));
        const initialPeak = await page.evaluate(() => peak());
        await page.evaluate(() => UnoPlayMedia.equalize(id,[0,0,0,0,0,-12,0,0,0,0,0]));
        await page.waitForFunction(peakBefore => peak() < peakBefore - 15, initialPeak);
        await page.evaluate(() => { UnoPlayMedia.pause(id); UnoPlayMedia.seek(id,1.5); });
        await page.waitForFunction(() => !UnoPlayMedia.state(id,"playing") && Math.abs(UnoPlayMedia.state(id,"position")-1.5) < .05);
        console.log("MP3 decode, FFT, ten-band EQ attenuation, pause and seek passed");
        await page.evaluate(() => start("clip.mp4"));
        await page.waitForFunction(() => UnoPlayMedia.state(id,"video") === 96 && UnoPlayMedia.state(id,"videoHeight") === 64 && UnoPlayMedia.state(id,"duration") > 1);
        await page.evaluate(() => UnoPlayMedia.toggleFullscreen(id));
        await page.waitForFunction(() => UnoPlayMedia.state(id,"fullscreen") === 1 && document.fullscreenElement?.controls);
        await page.evaluate(() => UnoPlayMedia.toggleFullscreen(id));
        await page.waitForFunction(() => !document.fullscreenElement && !document.querySelector("video").controls);
        await page.waitForFunction(() => signals.some(([kind])=>kind==="ended"));
        console.log("MP4 video decoded at 96 x 64, entered/exited native fullscreen and emitted ended");
        await page.evaluate(async () => {
            const blob = await (await fetch("tone.mp3")).blob();
            window.objectUrl = URL.createObjectURL(blob);
            await start(objectUrl);
        });
        await page.waitForFunction(() => UnoPlayMedia.state(id,"position") > .1);
        await page.evaluate(() => UnoPlayMedia.revoke(objectUrl));
        // Force the MSE path even on browsers that also ship a native HLS codec.
        await page.evaluate(() => {
            const native = HTMLMediaElement.prototype.canPlayType;
            HTMLMediaElement.prototype.canPlayType = function(type) {
                return /mpegurl/i.test(type) ? "" : native.call(this, type);
            };
        });
        let fetchedDecoder = false;
        page.on("response", response => {
            if (response.url().endsWith("/media/hls-1.7.4.min.mjs") && response.ok()) fetchedDecoder = true;
        });
        await page.evaluate(() => start("index.m3u8"));
        await page.waitForFunction(() => UnoPlayMedia.state(id,"position") > .2);
        assert.equal(fetchedDecoder, true, "HLS decoder must load on demand");
        console.log("Blob URL and MSE HLS playlist with real AAC segments passed");
        await page.evaluate(port=>start(`http://127.0.0.1:${port}/tone.mp3`),radio.address().port);
        await page.waitForFunction(() => UnoPlayMedia.state(id,"playing") && UnoPlayMedia.state(id,"position") > .1 && !UnoPlayMedia.state(id,"dsp"));
        assert.deepEqual(await page.evaluate(()=>signals.filter(([kind])=>kind==="error")),[]);
        await page.evaluate(()=>{ UnoPlayMedia.dispose(id); });
        assert.equal(await page.locator("video").count(), 0);
        assert.equal(await page.evaluate(()=>systemActions.size), 0);
        assert.equal(await page.evaluate(()=>navigator.mediaSession.metadata), null);
        console.log("Cross-origin radio fallback, error isolation and disposal passed");
    } finally {
        await browser?.close();
        await Promise.all([main,radio].map(server => new Promise(resolve=>server.close(resolve))));
        fs.rmSync(fixtures,{recursive:true,force:true});
    }
})().catch(error => { console.error(error); process.exitCode=1; });
