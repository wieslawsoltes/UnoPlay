// The native host is shared by Uno's DOM and Skia renderers. Each open owns its
// listeners and decoder; replacing a track invalidates pending asynchronous work.
(() => {
    "use strict";
    const frequencies = [60, 170, 310, 600, 1000, 3000, 6000, 12000, 14000, 16000];
    const players = new Map();
    const transportActions = ["play","pause","stop","previoustrack","nexttrack","seekto","seekforward","seekbackward"];
    let sessionOwner;
    function setAction(action, handler) {
        try { navigator.mediaSession.setActionHandler(action, handler); }
        catch (error) { if (error.name !== "NotSupportedError") throw error; }
    }

    class BrowserPlayer {
        constructor(notify) {
            this.notify = notify;
            this.volume = 1;
            this.balance = 0;
            this.eq = [0, ...frequencies.map(() => 0)];
        }

        attach(root) {
            this.root = root;
            root.dataset.unoplayMedia = "";
            Object.assign(root.style, {background:"#000", overflow:"hidden", width:"100%", height:"100%"});
        }

        releaseTrack() {
            this.listeners?.abort();
            this.hls?.destroy();
            this.hls = undefined;
            if (this.media) {
                this.media.pause();
                this.media.removeAttribute("src");
                this.media.load();
                this.media.remove();
            }
            this.source?.disconnect();
            this.source = undefined;
            this.media = undefined;
            this.processing = false;
        }

        open(url, direct = false) {
            this.releaseTrack();
            this.url = new URL(url, document.baseURI);
            const media = this.media = document.createElement("video");
            media.playsInline = true;
            media.preload = "metadata";
            media.style.cssText = "display:block;width:100%;height:100%;object-fit:contain";
            const remote = this.url.protocol !== "blob:" && this.url.origin !== location.origin;
            this.direct = direct;
            this.wantsPlay = false;
            if (remote && !direct) media.crossOrigin = "anonymous";
            this.root.replaceChildren(media);
            const signal = (this.listeners = new AbortController()).signal;
            for (const event of ["loadedmetadata", "durationchange", "play", "pause", "playing", "resize", "canplay", "seeked", "ratechange"]) {
                media.addEventListener(event, () => {
                    this.syncSession();
                    this.notify("state", "");
                }, {signal});
            }
            const fullscreenChanged = () => {
                const full = document.fullscreenElement === media || !!media.webkitDisplayingFullscreen;
                media.controls = full;
                media.style.pointerEvents = full ? "auto" : "";
                this.notify("state", "");
            };
            document.addEventListener("fullscreenchange", fullscreenChanged, {signal});
            media.addEventListener("webkitbeginfullscreen", fullscreenChanged, {signal});
            media.addEventListener("webkitendfullscreen", fullscreenChanged, {signal});
            media.addEventListener("ended", () => { this.syncSession(); this.notify("ended", ""); }, {signal});
            const hls = /\.m3u8?$/i.test(this.url.pathname) && !media.canPlayType("application/vnd.apple.mpegurl");
            media.addEventListener("error", () => {
                // Radio streams often permit native playback but not Web Audio.
                // Start a fresh, untainted element for direct audio output.
                if (remote && !direct && !hls && media.readyState === 0) {
                    const resume = this.wantsPlay;
                    this.open(this.url.href, true);
                    if (resume) this.play().catch(error => this.notify("error", error.message));
                } else {
                    this.notify("error", media.error?.message || "This media could not be decoded or reached.");
                }
            }, {signal});
            media.volume = this.volume;
            if (hls) {
                this.preparing = this.attachHls(media, this.url.href);
                // Report a loader error even when playback wasn't requested.
                this.preparing.catch(error => {
                    if (this.media === media) this.notify("error", error.message);
                });
            } else {
                this.preparing = null;
                media.src = this.url.href;
            }
            this.notify("state", "");
        }

        async attachHls(media, url) {
            const {default: Hls} = await import(new URL("media/hls-1.7.4.min.mjs", document.baseURI).href);
            if (media !== this.media) return;
            if (!Hls.isSupported()) throw new Error("This browser cannot decode HLS via Media Source Extensions.");
            const hls = this.hls = new Hls({enableWorker: true, capLevelToPlayerSize: true});
            hls.on(Hls.Events.ERROR, (_, data) => {
                if (data.fatal && media === this.media) {
                    this.notify("error", "HLS: " + data.details);
                    hls.destroy();
                    this.hls = undefined;
                }
            });
            hls.loadSource(url);
            hls.attachMedia(media);
        }

        ensureGraph() {
            if (this.direct || this.source || !globalThis.AudioContext) return;
            if (!this.context) {
                const ctx = this.context = new AudioContext({latencyHint: "playback"});
                this.preamp = ctx.createGain();
                // Upmix mono to stereo before the splitter's discrete channels.
                this.preamp.channelCount = 2;
                this.preamp.channelCountMode = "explicit";
                let previous = this.preamp;
                this.bands = frequencies.map(f => {
                    const band = ctx.createBiquadFilter();
                    band.type = "peaking";
                    band.frequency.value = Math.min(f, ctx.sampleRate / 2 - 1);
                    band.Q.value = 1.4;
                    previous.connect(band);
                    previous = band;
                    return band;
                });
                this.analyser = ctx.createAnalyser();
                this.analyser.fftSize = 2048;
                this.analyser.smoothingTimeConstant = 0.65;
                this.fft = new Uint8Array(this.analyser.frequencyBinCount);
                this.wave = new Uint8Array(this.analyser.fftSize);
                previous.connect(this.analyser);
                const splitter = ctx.createChannelSplitter(2);
                const merger = ctx.createChannelMerger(2);
                this.analyser.connect(splitter);
                this.channels = [0,1].map(i => {
                    const gain = ctx.createGain();
                    splitter.connect(gain, i);
                    gain.connect(merger, 0, i);
                    return gain;
                });
                this.output = ctx.createGain();
                merger.connect(this.output).connect(ctx.destination);
            }
            this.source = this.context.createMediaElementSource(this.media);
            this.source.connect(this.preamp);
            this.processing = true;
            this.media.volume = 1;
            this.updateMix();
        }

        updateMix() {
            if (!this.processing) {
                if (this.media) this.media.volume = this.volume;
                return;
            }
            const t = this.context.currentTime;
            const ramp = (param, value) => param.setTargetAtTime(value, t, .008);
            ramp(this.preamp.gain, 10 ** (this.eq[0] / 20));
            this.bands.forEach((b, i) => ramp(b.gain, this.eq[i + 1]));
            ramp(this.channels[0].gain, 1 - Math.max(0, this.balance));
            ramp(this.channels[1].gain, 1 + Math.min(0, this.balance));
            ramp(this.output.gain, this.volume);
        }

        activateSession() {
            if (!navigator.mediaSession || sessionOwner === this) return;
            sessionOwner = this;
            for (const action of transportActions) setAction(action, details => {
                if (sessionOwner !== this) return;
                if (action === "seekto") this.seek(details.seekTime);
                else if (action === "seekforward" || action === "seekbackward") {
                    const offset = details.seekOffset ?? 5;
                    this.seek(this.media.currentTime + (action === "seekforward" ? offset : -offset));
                } else this.notify("command", action);
            });
            this.publishMetadata();
            this.syncSession();
        }

        publishMetadata() {
            if (sessionOwner === this && globalThis.MediaMetadata)
                navigator.mediaSession.metadata = this.tags?.title ? new MediaMetadata(this.tags) : null;
        }

        syncSession() {
            if (sessionOwner !== this) return;
            const m = this.media, session = navigator.mediaSession;
            session.playbackState = !m || m.ended ? "none" : m.paused ? "paused" : "playing";
            if (!session.setPositionState) return;
            if (Number.isFinite(m?.duration) && m.duration > 0 && m.playbackRate > 0)
                session.setPositionState({duration:m.duration, playbackRate:m.playbackRate,
                    position:Math.min(m.duration, Math.max(0, m.currentTime))});
            else session.setPositionState();
        }

        async play() {
            const media = this.media;
            if (!media) return;
            this.wantsPlay = true;
            this.activateSession();
            this.ensureGraph();
            const resumed = this.processing ? this.context.resume() : undefined;
            try {
                if (this.preparing) await this.preparing;
                if (media !== this.media || !this.wantsPlay) return;
                await Promise.all([media.play(), resumed]);
            } catch (error) {
                if (media === this.media && this.wantsPlay) throw error;
            }
        }

        pause() {
            this.wantsPlay = false;
            this.media?.pause();
        }

        seek(value) {
            const ranges = this.media?.seekable;
            if (!ranges?.length || !Number.isFinite(value)) return;
            let best = ranges.start(0);
            for (let i = 0; i < ranges.length; i++) {
                const point = Math.min(ranges.end(i), Math.max(ranges.start(i), value));
                if (Math.abs(point - value) < Math.abs(best - value)) best = point;
            }
            this.media.currentTime = best;
        }

        sample(buffer, waveform) {
            if (!this.processing || this.media.paused) {
                if (this.bins?.length !== buffer.length) this.bins = new Uint8Array(buffer.length);
                buffer.set(this.bins.fill(waveform ? 128 : 0));
                return;
            }
            if (this.bins?.length !== buffer.length) this.bins = new Uint8Array(buffer.length);
            if (waveform) {
                this.analyser.getByteTimeDomainData(this.wave);
                for (let i = 0; i < this.bins.length; i++) {
                    this.bins[i] = this.wave[Math.floor(i * this.wave.length / this.bins.length)];
                }
            } else {
                this.analyser.getByteFrequencyData(this.fft);
                const scale = this.analyser.fftSize / this.context.sampleRate;
                let from = 0;
                for (let i = 0; i < this.bins.length; i++) {
                    const to = Math.min(this.fft.length, Math.max(from + 1, Math.round(40 * 400 ** ((i + 1) / this.bins.length) * scale)));
                    let peak = 0;
                    for (let j = from; j < to; j++) peak = Math.max(peak, this.fft[j]);
                    this.bins[i] = peak;
                    from = to;
                }
            }
            buffer.set(this.bins);
        }

        destroy() {
            if (sessionOwner === this) {
                for (const action of transportActions) setAction(action, null);
                navigator.mediaSession.metadata = null;
                navigator.mediaSession.playbackState = "none";
                navigator.mediaSession.setPositionState?.();
                sessionOwner = undefined;
            }
            this.releaseTrack();
            this.context?.close();
        }
    }

    globalThis.UnoPlayMedia = {
        create(id, notify) { players.set(id, new BrowserPlayer(notify)); },
        attach(root) { players.get(root.id).attach(root); },
        dispose(id) { players.get(id)?.destroy(); players.delete(id); },
        open(id, url) { players.get(id).open(url); },
        play(id) { return players.get(id).play(); },
        pause(id) { players.get(id).pause(); },
        seek(id, seconds) { players.get(id).seek(seconds); },
        mediaSession(id, active) {
            const player = players.get(id);
            if (sessionOwner === player) sessionOwner = undefined;
            if (active) player.activateSession();
        },
        metadata(id, title, artist, album) {
            const player = players.get(id);
            player.tags = {title, artist, album};
            player.publishMetadata();
        },
        volume(id, volume) { const p = players.get(id); p.volume = volume; p.updateMix(); },
        balance(id, balance) { const p = players.get(id); p.balance = balance; p.updateMix(); },
        equalize(id, eq) { const p = players.get(id); p.eq = eq; p.updateMix(); },
        sample(id, bytes, waveform) { players.get(id).sample(bytes, waveform); },
        async toggleFullscreen(id) {
            const m = players.get(id)?.media;
            if (!m || !m.videoWidth) throw Error("The current source has no video.");
            if (document.fullscreenElement === m) await document.exitFullscreen();
            else if (m.webkitDisplayingFullscreen) m.webkitExitFullscreen();
            else if (m.requestFullscreen && document.fullscreenEnabled) await m.requestFullscreen();
            else if (m.webkitEnterFullscreen) m.webkitEnterFullscreen();
            else throw Error("Fullscreen video is unavailable in this browser.");
        },
        state(id, key) {
            const p = players.get(id);
            const m = p.media;
            if (!m) return 0;
            switch (key) {
                case "position": return m.currentTime || 0;
                case "duration": return Number.isFinite(m.duration) ? m.duration : 0;
                case "playing": return Number(!m.paused && !m.ended);
                case "video": return m.videoWidth;
                case "videoHeight": return m.videoHeight;
                case "fullscreen": return Number(document.fullscreenElement === m || !!m.webkitDisplayingFullscreen);
                case "seekable": return m.seekable.length;
                case "dsp": return Number(!p.direct && !!globalThis.AudioContext);
                default: return 0;
            }
        },
        pick() {
            return new Promise((resolve, reject) => {
                const input = document.createElement("input");
                input.type = "file";
                input.multiple = true;
                input.accept = "audio/*,video/*,.mp3,.mp4,.m4a,.aac,.flac,.mkv,.wav,.webm,.ogg,.opus,.mov";
                input.addEventListener("change", () => resolve(JSON.stringify(Array.from(input.files, f =>
                    [f.name, URL.createObjectURL(f), f.size, f.lastModified]))), {once: true});
                input.addEventListener("cancel", () => resolve("[]"), {once: true});
                try { if (input.showPicker) input.showPicker(); else input.click(); }
                catch (error) { reject(error); }
            });
        },
        createBlob() { return {parts: []}; },
        appendBlob(blob, data, count) { blob.parts.push(data.slice(0, count)); },
        finishBlob(blob, type) { return URL.createObjectURL(new Blob(blob.parts, {type})); },
        revoke(url) { URL.revokeObjectURL(url); }
    };
})();
