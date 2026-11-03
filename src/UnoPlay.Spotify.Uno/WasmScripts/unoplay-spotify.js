// Thin lifecycle bridge to Spotify's hosted Web Playback SDK. Authorization and
// Web API calls belong to UnoPlay.Spotify, and audio stays inside Spotify's SDK.
(() => {
    "use strict";
    const players = new Map();
    const sdkUrl = "https://sdk.scdn.co/spotify-player.js";
    const timeoutMs = 20000;
    let sdkLoading;

    function loadSdk() {
        if (globalThis.Spotify?.Player) return Promise.resolve();
        if (sdkLoading) return sdkLoading;
        const loading = new Promise((resolve, reject) => {
            const previous = globalThis.onSpotifyWebPlaybackSDKReady;
            const script = document.createElement("script");
            let settled = false;
            const finish = error => {
                if (settled) return;
                settled = true;
                clearTimeout(timer);
                script.onerror = null;
                script.onload = null;
                if (globalThis.onSpotifyWebPlaybackSDKReady === ready)
                    globalThis.onSpotifyWebPlaybackSDKReady = previous;
                if (error) {
                    script.remove();
                    reject(error);
                } else resolve();
            };
            const ready = () => {
                if (!globalThis.Spotify?.Player) {
                    finish(new Error("The Spotify playback SDK did not initialize."));
                    return;
                }
                finish();
                if (typeof previous === "function") previous();
            };
            const timer = setTimeout(() => finish(new Error("Loading the Spotify playback SDK timed out. Check the connection and try again.")), timeoutMs);
            globalThis.onSpotifyWebPlaybackSDKReady = ready;
            script.src = sdkUrl;
            script.async = true;
            script.onerror = () => finish(new Error("The Spotify playback SDK could not be loaded. Check the connection or browser content blocking."));
            script.onload = () => { if (globalThis.Spotify?.Player) finish(); };
            document.head.append(script);
        });
        sdkLoading = loading;
        // Release failed loads so a later explicit connection can retry.
        loading.catch(() => { if (sdkLoading === loading) sdkLoading = undefined; });
        return loading;
    }

    function externalUrl(uri) {
        const match = /^spotify:(track|album|artist|episode|show|playlist):([A-Za-z0-9]+)$/.exec(uri || "");
        return match ? `https://open.spotify.com/${match[1]}/${match[2]}` : "";
    }

    class BrowserSpotifyPlayer {
        constructor(notify) {
            this.notify = notify;
            this.generation = 0;
            this.tokenSequence = 0;
            this.tokens = new Map();
            this.listeners = [];
            this.name = "UnoPlay";
            this.volume = 1;
            this.deviceId = null;
            this.state = null;
            this.disposed = false;
        }

        current(generation) { return !this.disposed && generation === this.generation; }

        send(kind, value) {
            if (!this.disposed) this.notify(kind, value);
        }

        connect() {
            if (this.disposed) return Promise.reject(new Error("The Spotify player has been disposed."));
            if (this.deviceId) return Promise.resolve(this.deviceId);
            if (this.connection) return this.connection.promise;
            this.disconnect(new Error("The Spotify connection was replaced."), false);
            const generation = this.generation;
            let resolveConnection, rejectConnection;
            const promise = new Promise((resolve, reject) => {
                resolveConnection = resolve;
                rejectConnection = reject;
            });
            const timer = setTimeout(() => this.fail("Connecting the Spotify playback device timed out. Try connecting again.", generation), timeoutMs);
            this.connection = {promise, resolve: resolveConnection, reject: rejectConnection, timer};
            this.start(generation).catch(error => this.fail(error.message || "Spotify could not connect.", generation));
            return promise;
        }

        async start(generation) {
            await loadSdk();
            if (!this.current(generation)) return;
            const player = new Spotify.Player({
                name: this.name,
                volume: this.volume,
                enableMediaSession: true,
                getOAuthToken: callback => this.requestToken(callback, generation)
            });
            this.player = player;
            const listen = (name, action) => {
                const listener = value => { if (this.current(generation)) action(value); };
                player.addListener(name, listener);
                this.listeners.push([name, listener]);
            };
            listen("ready", ({device_id}) => {
                if (!device_id) { this.fail("Spotify returned an empty playback device identifier.", generation); return; }
                this.deviceId = device_id;
                const connection = this.connection;
                this.connection = null;
                if (connection) {
                    clearTimeout(connection.timer);
                    connection.resolve(device_id);
                }
                this.send("ready", device_id);
            });
            listen("not_ready", () => {
                this.deviceId = null;
                this.state = null;
                this.send("not_ready", "");
                this.send("state", "null");
            });
            listen("player_state_changed", state => {
                this.state = state;
                this.send("state", JSON.stringify(this.normalize(state)));
            });
            listen("autoplay_failed", () => this.send("autoplay", "Select Play to allow Spotify audio in this browser."));
            for (const event of ["initialization_error", "authentication_error", "account_error"])
                listen(event, ({message}) => this.fail(message || "Spotify playback could not initialize.", generation));
            listen("playback_error", ({message}) => this.send("error", message || "Spotify could not play this item."));
            const connected = await player.connect();
            if (this.current(generation) && !connected) this.fail("Spotify declined the playback device connection. Sign in and try again.", generation);
        }

        fail(message, generation) {
            if (!this.current(generation)) return;
            this.disconnect(new Error(message));
            this.send("error", message);
        }

        requestToken(callback, generation) {
            if (!this.current(generation)) return;
            const requestId = `${generation}-${++this.tokenSequence}`;
            const timer = setTimeout(() => {
                this.tokens.delete(requestId);
                this.fail("Spotify authorization timed out. Sign in again.", generation);
            }, timeoutMs);
            this.tokens.set(requestId, {callback, generation, timer});
            this.send("token", requestId);
        }

        answerToken(requestId, token, error) {
            const request = this.tokens.get(requestId);
            if (!request) return;
            clearTimeout(request.timer);
            this.tokens.delete(requestId);
            if (!this.current(request.generation)) return;
            if (error || !token) {
                this.fail(error || "Spotify authorization returned an empty access token.", request.generation);
                return;
            }
            request.callback(token);
        }

        normalize(state) {
            if (!state) return null;
            const track = state.track_window?.current_track;
            const item = track ? {
                ...track,
                duration_ms: state.duration ?? track.duration_ms ?? 0,
                external_urls: {spotify: externalUrl(track.uri)},
                artists: (track.artists || []).map(artist => ({...artist, external_urls: {spotify: externalUrl(artist.uri)}})),
                album: track.album ? {...track.album, external_urls: {spotify: externalUrl(track.album.uri)}} : null
            } : null;
            return {
                device: {
                    id: this.deviceId,
                    name: this.name,
                    type: "Computer",
                    is_active: true,
                    is_restricted: false,
                    volume_percent: Math.round(this.volume * 100),
                    supports_volume: true
                },
                item,
                is_playing: !state.paused,
                progress_ms: Math.max(0, state.position || 0),
                repeat_state: ["off", "context", "track"][state.repeat_mode] || "off",
                shuffle_state: !!state.shuffle,
                context: state.context,
                actions: state.disallows || {},
                currently_playing_type: track?.type || "unknown"
            };
        }

        activePlayer() {
            if (this.disposed) throw new Error("The Spotify player has been disposed.");
            if (!this.player) throw new Error("Connect the Spotify browser player first.");
            return this.player;
        }

        async currentState() {
            const generation = this.generation;
            const state = await this.activePlayer().getCurrentState();
            if (!this.current(generation)) return "null";
            this.state = state;
            return JSON.stringify(this.normalize(state));
        }

        async getVolume() {
            if (!this.player) return this.volume;
            const generation = this.generation;
            const volume = await this.activePlayer().getVolume();
            if (this.current(generation)) this.volume = volume;
            return volume;
        }

        async setVolume(volume) {
            if (!Number.isFinite(volume) || volume < 0 || volume > 1) throw new Error("Spotify volume must be between 0 and 1.");
            if (this.disposed) throw new Error("The Spotify player has been disposed.");
            const generation = this.generation;
            if (this.player) await this.player.setVolume(volume);
            if (this.current(generation)) this.volume = volume;
        }

        async setName(name) {
            if (!name?.trim()) throw new Error("The Spotify device name cannot be empty.");
            if (this.disposed) throw new Error("The Spotify player has been disposed.");
            const generation = this.generation;
            if (this.player) await this.player.setName(name);
            if (this.current(generation)) this.name = name;
        }

        disconnect(reason = new Error("The Spotify player was disconnected."), announce = true) {
            ++this.generation;
            const connection = this.connection;
            this.connection = null;
            if (connection) {
                clearTimeout(connection.timer);
                connection.reject(reason);
            }
            for (const request of this.tokens.values()) clearTimeout(request.timer);
            this.tokens.clear();
            const player = this.player;
            this.player = null;
            this.deviceId = null;
            this.state = null;
            if (player) {
                for (const [name, listener] of this.listeners) player.removeListener(name, listener);
                player.disconnect();
            }
            this.listeners = [];
            if (announce) {
                this.send("not_ready", "");
                this.send("state", "null");
            }
        }

        dispose() {
            this.disposed = true;
            this.disconnect();
            this.notify = null;
        }
    }

    const get = id => {
        const player = players.get(id);
        if (!player) throw new Error("The Spotify player is unavailable.");
        return player;
    };
    globalThis.UnoPlaySpotifySdk = {
        create(id, notify) {
            players.get(id)?.dispose();
            players.set(id, new BrowserSpotifyPlayer(notify));
        },
        connect: id => get(id).connect(),
        disconnect: id => get(id).disconnect(),
        dispose(id) { players.get(id)?.dispose(); players.delete(id); },
        answerToken: (id, requestId, token, error) => players.get(id)?.answerToken(requestId, token, error),
        currentState: id => get(id).currentState(),
        getVolume: id => get(id).getVolume(),
        setVolume: (id, volume) => get(id).setVolume(volume),
        setName: (id, name) => get(id).setName(name),
        pause: id => get(id).activePlayer().pause(),
        resume: id => get(id).activePlayer().resume(),
        togglePlay: id => get(id).activePlayer().togglePlay(),
        seek(id, milliseconds) {
            if (!Number.isFinite(milliseconds) || milliseconds < 0) return Promise.reject(new Error("The Spotify seek position must be positive."));
            return get(id).activePlayer().seek(Math.round(milliseconds));
        },
        previous: id => get(id).activePlayer().previousTrack(),
        next: id => get(id).activePlayer().nextTrack(),
        activate: id => get(id).activePlayer().activateElement()
    };
})();
