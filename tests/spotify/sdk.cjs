// Explicit SDK doubles exercise the bridge, without a Spotify account or DRM.
// Run after npm ci --prefix tests/browser with NODE_PATH=tests/browser/node_modules.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const {createServer} = require("node:http");
const {chromium} = require("playwright");

const bridge = fs.readFileSync(path.resolve(__dirname, "../../src/UnoPlay.Spotify.Uno/WasmScripts/unoplay-spotify.js"));
const fakeSdk = `
window.sdkInstances = [];
window.Spotify = { Player: class {
    constructor(options) {
        this.options = options;
        this.listeners = new Map();
        this.calls = [];
        this.tokens = [];
        this.volume = options.volume;
        this.name = options.name;
        this.state = null;
        this.index = sdkInstances.length;
        sdkInstances.push(this);
    }
    addListener(name, callback) { this.listeners.set(name, callback); return true; }
    removeListener(name) { this.listeners.delete(name); return true; }
    emit(name, value) { this.listeners.get(name)?.(value); }
    connect() {
        this.calls.push('connect');
        if (window.sdkMode === 'false') return Promise.resolve(false);
        if (window.sdkMode === 'never') return new Promise(() => {});
        return new Promise(resolve => this.options.getOAuthToken(token => {
            this.tokens.push(token);
            queueMicrotask(() => { this.emit('ready', {device_id:'device-' + this.index}); resolve(true); });
        }));
    }
    disconnect() { this.calls.push('disconnect'); }
    pause() { this.calls.push('pause'); return Promise.resolve(); }
    resume() { this.calls.push('resume'); return Promise.resolve(); }
    togglePlay() { this.calls.push('togglePlay'); return Promise.resolve(); }
    seek(position) { this.calls.push(['seek',position]); return Promise.resolve(); }
    previousTrack() { this.calls.push('previousTrack'); return Promise.resolve(); }
    nextTrack() { this.calls.push('nextTrack'); return Promise.resolve(); }
    activateElement() { this.calls.push('activateElement'); return Promise.resolve(); }
    setName(name) { this.name = name; return Promise.resolve(); }
    setVolume(volume) { this.volume = volume; return Promise.resolve(); }
    getVolume() { return Promise.resolve(this.volume); }
    getCurrentState() { return Promise.resolve(this.state); }
}};
window.onSpotifyWebPlaybackSDKReady();
`;

async function startServer() {
    const server = createServer((req, res) => {
        if (req.url === "/bridge.js") {
            res.setHeader("Content-Type", "text/javascript");
            res.end(bridge);
        } else {
            res.setHeader("Content-Type", "text/html");
            res.end('<!doctype html><title>Spotify SDK bridge tests</title><script src="/bridge.js"></script>');
        }
    });
    await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
    return server;
}

(async () => {
    const server = await startServer();
    let browser;
    try {
        browser = await chromium.launch({executablePath: process.env.CHROMIUM_PATH, args: ["--no-sandbox"]});
        const page = await browser.newPage();
        const pageErrors = [];
        page.on("pageerror", error => pageErrors.push(error.message));
        let loads = 0;
        await page.route("https://sdk.scdn.co/spotify-player.js", route => {
            ++loads;
            return route.fulfill({contentType: "text/javascript", body: fakeSdk});
        });
        const url = `http://127.0.0.1:${server.address().port}/`;
        await page.goto(url);
        await page.evaluate(() => {
            window.signals = [];
            window.tokenCount = 0;
            window.create = id => UnoPlaySpotifySdk.create(id, (kind, value) => {
                signals.push([id, kind, value]);
                if (kind === "token") UnoPlaySpotifySdk.answerToken(id, value, "test-access-" + ++tokenCount, "");
            });
            create("one");
            create("two");
        });
        assert.equal(loads, 0, "Creating a player must leave the hosted SDK unloaded");
        assert.deepEqual(await page.evaluate(async () => {
            await UnoPlaySpotifySdk.setVolume("one", .35);
            await UnoPlaySpotifySdk.setName("one", "UnoPlay test");
            return Promise.all([UnoPlaySpotifySdk.connect("one"), UnoPlaySpotifySdk.connect("two"), UnoPlaySpotifySdk.connect("one")]);
        }), ["device-0", "device-1", "device-0"]);
        assert.equal(loads, 1, "Concurrent players share one SDK download");
        assert.equal(await page.evaluate(() => sdkInstances.length), 2);
        assert.deepEqual(await page.evaluate(() => [sdkInstances[0].name, sdkInstances[0].volume, sdkInstances[0].options.enableMediaSession]), ["UnoPlay test", .35, true]);

        await page.evaluate(async () => {
            const api = UnoPlaySpotifySdk;
            await api.pause("one"); await api.resume("one"); await api.togglePlay("one");
            await api.seek("one", 1200.4); await api.previous("one"); await api.next("one");
            await api.activate("one"); await api.setName("one", "UnoPlay renamed"); await api.setVolume("one", .6);
            sdkInstances[0].options.getOAuthToken(token => sdkInstances[0].tokens.push(token));
        });
        assert.deepEqual(await page.evaluate(() => sdkInstances[0].calls), ["connect", "pause", "resume", "togglePlay", ["seek",1200], "previousTrack", "nextTrack", "activateElement"]);
        assert.equal(await page.evaluate(() => UnoPlaySpotifySdk.getVolume("one")), .6);
        assert.equal(await page.evaluate(() => sdkInstances[0].tokens.length), 2, "SDK refresh requests obtain a fresh application token");

        const state = await page.evaluate(async () => {
            sdkInstances[0].state = {
                paused: false, position: 1200, duration: 180000, repeat_mode: 2, shuffle: true,
                context: {uri:"spotify:album:album1"}, disallows:{skipping_next:true,pausing:false},
                track_window: {current_track: {id:"track1",uri:"spotify:track:track1",name:"SDK fixture",type:"track",is_playable:true,
                    artists:[{uri:"spotify:artist:artist1",name:"Test artist"}], album:{uri:"spotify:album:album1",name:"Test album",images:[{url:"https://i.scdn.co/image/fixture"}]}}}
            };
            sdkInstances[0].emit("player_state_changed", sdkInstances[0].state);
            return JSON.parse(await UnoPlaySpotifySdk.currentState("one"));
        });
        assert.equal(state.item.duration_ms, 180000);
        assert.equal(state.item.external_urls.spotify, "https://open.spotify.com/track/track1");
        assert.equal(state.item.artists[0].external_urls.spotify, "https://open.spotify.com/artist/artist1");
        assert.equal(state.device.name, "UnoPlay renamed");
        assert.equal(state.device.volume_percent, 60);
        assert.equal(state.repeat_state, "track");
        assert.equal(state.shuffle_state, true);
        assert.equal(state.actions.skipping_next, true);
        assert.equal(state.context.uri, "spotify:album:album1");
        assert.equal(state.progress_ms, 1200);
        await page.evaluate(() => {
            sdkInstances[0].emit("autoplay_failed");
            sdkInstances[0].emit("playback_error", {message:"An item is unavailable"});
            sdkInstances[0].emit("not_ready");
        });
        assert.ok(await page.evaluate(() => signals.some(signal => signal[1] === "autoplay")));
        assert.ok(await page.evaluate(() => signals.some(signal => signal[1] === "error" && signal[2] === "An item is unavailable")));
        assert.ok(await page.evaluate(() => signals.some(signal => signal[1] === "state" && signal[2] === "null")));
        await page.evaluate(() => sdkInstances[0].emit("ready", {device_id:"restored-device"}));
        assert.equal(await page.evaluate(() => UnoPlaySpotifySdk.connect("one")), "restored-device");

        await page.evaluate(() => {
            window.staleReady = sdkInstances[0].listeners.get("ready");
            UnoPlaySpotifySdk.dispose("one");
            window.signalCountAfterDispose = signals.length;
            staleReady({device_id:"late-device"});
        });
        assert.equal(await page.evaluate(() => signals.length), await page.evaluate(() => signalCountAfterDispose));
        assert.equal(await page.evaluate(() => sdkInstances[0].listeners.size), 0);
        assert.equal(await page.evaluate(() => sdkInstances[0].calls.at(-1)), "disconnect");

        for (const errorName of ["initialization_error", "authentication_error", "account_error"]) {
            await page.evaluate(name => sdkInstances.at(-1).emit(name, {message:name}), errorName);
            assert.equal(await page.evaluate(() => sdkInstances.at(-1).listeners.size), 0);
            assert.match(await page.evaluate(() => UnoPlaySpotifySdk.connect("two")), /^device-/);
        }
        await page.evaluate(() => {
            UnoPlaySpotifySdk.disconnect("two");
            window.sdkMode = "false";
        });
        assert.match(await page.evaluate(() => UnoPlaySpotifySdk.connect("two").catch(error => error.message)), /declined/);
        await page.evaluate(() => { window.sdkMode = undefined; });
        assert.match(await page.evaluate(() => UnoPlaySpotifySdk.connect("two")), /^device-/);

        // A delayed .NET token result must not reach a replaced native SDK instance.
        await page.evaluate(() => {
            UnoPlaySpotifySdk.create("pending", (kind, value) => {
                if (kind === "token") window.pendingTokenId = value;
            });
            window.pendingResult = UnoPlaySpotifySdk.connect("pending").catch(error => error.message);
        });
        await page.waitForFunction(() => window.pendingTokenId);
        await page.evaluate(() => {
            window.pendingInstance = sdkInstances.at(-1);
            UnoPlaySpotifySdk.dispose("pending");
            UnoPlaySpotifySdk.answerToken("pending", pendingTokenId, "late-test-token", "");
        });
        assert.match(await page.evaluate(() => pendingResult), /disconnected/);
        assert.equal(await page.evaluate(() => pendingInstance.tokens.length), 0);

        // Load failure is recoverable on the next explicit connection attempt.
        const retry = await browser.newPage();
        let attempts = 0;
        await retry.route("https://sdk.scdn.co/spotify-player.js", route => {
            ++attempts;
            return attempts === 1 ? route.abort() : route.fulfill({contentType:"text/javascript",body:fakeSdk});
        });
        await retry.goto(url);
        await retry.evaluate(() => UnoPlaySpotifySdk.create("retry", (kind, value) => {
            if (kind === "token") UnoPlaySpotifySdk.answerToken("retry", value, "retry-test-token", "");
        }));
        assert.match(await retry.evaluate(() => UnoPlaySpotifySdk.connect("retry").catch(error => error.message)), /could not be loaded/);
        assert.equal(await retry.evaluate(() => UnoPlaySpotifySdk.connect("retry")), "device-0");
        assert.equal(attempts, 2);

        await retry.clock.install();
        await retry.evaluate(() => {
            UnoPlaySpotifySdk.disconnect("retry");
            window.sdkMode = "never";
            window.timeoutResult = UnoPlaySpotifySdk.connect("retry").catch(error => error.message);
        });
        await retry.clock.runFor(21000);
        assert.match(await retry.evaluate(() => timeoutResult), /timed out/);
        assert.equal(await retry.evaluate(() => sdkInstances.at(-1).listeners.size), 0);
        assert.deepEqual(pageErrors, []);
        console.log("Spotify SDK bridge passed lazy loading, single-flight connect, token renewal, all transport methods, state mapping, reconnect, autoplay/errors, timeout, retry and disposal checks.");
    } finally {
        await browser?.close();
        await new Promise(resolve => server.close(resolve));
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
