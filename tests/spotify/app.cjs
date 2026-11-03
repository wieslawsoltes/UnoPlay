// Runs the published Uno UI through PKCE redirects with explicit Spotify API and SDK fixtures.
// Real-account streaming is a separate manual check because it needs Premium and app credentials.
const assert = require("node:assert/strict");
const { createHash } = require("node:crypto");

const clientId = "00000000000000000000000000000000";
const playlistId = "2222222222222222222222";
const trackId = "1111111111111111111111";
const album = { id: "3333333333333333333333", name: "Fixture album", uri: "spotify:album:3333333333333333333333", images: [], artists: [] };
const track = { id: trackId, uri: `spotify:track:${trackId}`, name: "Fixture song", type: "track", duration_ms: 180000,
    artists: [{ id: "4444444444444444444444", name: "Fixture artist", uri: "spotify:artist:4444444444444444444444" }],
    album, is_playable: true, is_local: false, external_urls: { spotify: `https://open.spotify.com/track/${trackId}` } };
const playlist = { id: playlistId, uri: `spotify:playlist:${playlistId}`, name: "Studio collection", description: "Fixture playlist",
    owner: { id: "fixture-user", display_name: "UnoPlay Tester" }, public: false, collaborative: false,
    snapshot_id: "snapshot-one", items: { total: 51 }, images: [], external_urls: { spotify: `https://open.spotify.com/playlist/${playlistId}` } };
const device = { id: "fixture-device", name: "UnoPlay fixture browser", type: "Computer", is_active: true,
    is_restricted: false, volume_percent: 75, supports_volume: true };
const sdkSource = `
window.spotifyFixtureCalls = [];
window.Spotify = { Player: class {
  constructor(options) { this.options = options; this.listeners = new Map(); this.state = null; this.volume = .75; window.spotifyFixturePlayer = this; }
  addListener(name, callback) { this.listeners.set(name, callback); return true; }
  removeListener(name) { this.listeners.delete(name); return true; }
  emit(name, value) { this.listeners.get(name)?.(value); }
  connect() { return new Promise(resolve => this.options.getOAuthToken(token => { spotifyFixtureCalls.push(['token', token]); this.emit('ready', {device_id: 'fixture-device'}); resolve(true); })); }
  disconnect() { spotifyFixtureCalls.push(['disconnect']); }
  start(item) { this.state = {paused:false, position:0, duration:item.duration_ms, repeat_mode:0, shuffle:false, context:{uri:'spotify:playlist:${playlistId}'}, disallows:{}, track_window:{current_track:item, previous_tracks:[], next_tracks:[]}}; this.emit('player_state_changed', this.state); }
  change(paused) { if(this.state) { this.state.paused = paused; this.emit('player_state_changed', this.state); } }
  pause() { spotifyFixtureCalls.push(['pause']); this.change(true); return Promise.resolve(); }
  resume() { spotifyFixtureCalls.push(['resume']); this.change(false); return Promise.resolve(); }
  togglePlay() { this.change(!this.state?.paused); return Promise.resolve(); }
  seek(position) { spotifyFixtureCalls.push(['seek',position]); if(this.state) { this.state.position=position; this.emit('player_state_changed',this.state); } return Promise.resolve(); }
  previousTrack() { spotifyFixtureCalls.push(['previous']); return Promise.resolve(); }
  nextTrack() { spotifyFixtureCalls.push(['next']); return Promise.resolve(); }
  activateElement() { spotifyFixtureCalls.push(['activate']); return Promise.resolve(); }
  getVolume() { return Promise.resolve(this.volume); }
  setVolume(value) { this.volume=value; spotifyFixtureCalls.push(['volume',value]); return Promise.resolve(); }
  setName(name) { this.name=name; return Promise.resolve(); }
  getCurrentState() { return Promise.resolve(this.state); }
}};
window.onSpotifyWebPlaybackSDKReady();
`;

async function verifySpotify(page, appUrl) {
    const requests = [];
    const grants = [];
    let authorization, profileCalls = 0, created = false, authorizationOutcome = "success";
    const json = (route, body, status = 200) => route.fulfill({ status, headers: {
        "Content-Type": "application/json", "Access-Control-Allow-Origin": "*",
        "Access-Control-Allow-Headers": "authorization,content-type", "Access-Control-Allow-Methods": "GET,POST,PUT,DELETE,OPTIONS"
    }, body: body === null ? "" : JSON.stringify(body) });
    await page.route("https://sdk.scdn.co/spotify-player.js", route => route.fulfill({ contentType: "text/javascript", body: sdkSource }));
    await page.route("https://accounts.spotify.com/authorize?**", async route => {
        authorization = new URL(route.request().url());
        assert.equal(authorization.searchParams.get("client_id"), clientId);
        assert.equal(authorization.searchParams.get("response_type"), "code");
        assert.equal(authorization.searchParams.get("code_challenge_method"), "S256");
        assert.equal(authorization.searchParams.get("redirect_uri"), appUrl);
        assert.ok(authorization.searchParams.get("state").length >= 32);
        const callback = new URL(appUrl);
        callback.searchParams.set(authorizationOutcome === "denied" ? "error" : "code",
            authorizationOutcome === "denied" ? "access_denied" : "fixture-authorization-code");
        callback.searchParams.set("state", authorizationOutcome === "mismatch" ? "incorrect-state" : authorization.searchParams.get("state"));
        await route.fulfill({ contentType: "text/html", body: `<!doctype html><title>Spotify authorization fixture</title><script>location.replace(${JSON.stringify(callback.href)})</script>` });
    });
    await page.route("https://accounts.spotify.com/api/token", async route => {
        if (route.request().method() === "OPTIONS") return json(route, null, 204);
        const body = new URLSearchParams(route.request().postData());
        grants.push(body.get("grant_type"));
        assert.equal(body.get("client_id"), clientId);
        assert.equal(body.has("client_secret"), false);
        if (body.get("grant_type") === "authorization_code") {
            assert.equal(body.get("code"), "fixture-authorization-code");
            assert.equal(createHash("sha256").update(body.get("code_verifier")).digest("base64url"), authorization.searchParams.get("code_challenge"));
        } else {
            assert.equal(body.get("refresh_token"), "fixture-refresh");
        }
        return json(route, { access_token: "fixture-access-" + grants.length, refresh_token: "fixture-refresh", token_type: "Bearer", expires_in: 3600, scope: "streaming playlist-read-private playlist-modify-private" });
    });
    await page.route("https://api.spotify.com/v1/**", async route => {
        const request = route.request(), url = new URL(request.url()), method = request.method(), pathname = url.pathname;
        if (method === "OPTIONS") return json(route, null, 204);
        assert.match(request.headers().authorization || "", /^Bearer fixture-access-/);
        let body;
        try { body = request.postDataJSON(); } catch { body = request.postData(); }
        requests.push({ method, pathname, query: Object.fromEntries(url.searchParams), body });
        if (pathname === "/v1/me") {
            if (++profileCalls === 1) return json(route, {error:{status:401,message:"Expired test token"}}, 401);
            return json(route, {id:"fixture-user",display_name:"UnoPlay Tester",external_urls:{spotify:"https://open.spotify.com/user/fixture-user"}});
        }
        if (pathname === "/v1/me/playlists" && method === "POST") { created = true; return json(route, {...playlist,name:body.name,items:{total:0}}, 201); }
        if (pathname === "/v1/me/playlists") return json(route, {items:[playlist],offset:0,limit:50,total:1,next:null});
        if (pathname === `/v1/playlists/${playlistId}`) return json(route, {...playlist, name: created ? "Created in UnoPlay" : playlist.name});
        if (pathname === `/v1/playlists/${playlistId}/items` && method === "GET") {
            const offset = Number(url.searchParams.get("offset") || 0);
            const items = created ? [] : offset === 50 ? [{item:{...track,name:"Final fixture track"},added_at:"2026-01-01T00:00:00Z"}]
                : Array.from({length:50}, (_, i) => ({item:{...track,name:i === 0 ? track.name : `Fixture song ${i + 1}`},added_at:"2026-01-01T00:00:00Z"}));
            return json(route, {items,offset,limit:50,total:created ? 0 : 51,next:!created && offset === 0 ? `https://api.spotify.com/v1/playlists/${playlistId}/items?offset=50&limit=50` : null});
        }
        if (pathname.endsWith("/items") && method !== "GET") return json(route,{snapshot_id:"snapshot-two"});
        if (pathname === "/v1/me/tracks") return json(route,{items:[{track,added_at:"2026-01-01T00:00:00Z"}],offset:0,limit:50,total:1,next:null});
        if (pathname === "/v1/search") return json(route,{tracks:{items:[track],offset:0,limit:10,total:1,next:null},playlists:{items:[],total:0},albums:{items:[],total:0},artists:{items:[],total:0}});
        if (pathname === "/v1/me/player/devices") return json(route,{devices:[device]});
        if (pathname === "/v1/me/player/queue" && method === "GET") return json(route,{currently_playing:track,queue:[track]});
        if (pathname === "/v1/me/player" && method === "GET") return json(route,null,204);
        if (pathname === "/v1/me/player/play") {
            await page.evaluate(item => window.spotifyFixturePlayer.start(item), track);
            return json(route,null,204);
        }
        if (pathname === "/v1/me/library" || pathname.startsWith("/v1/me/player")) return json(route,null,204);
        return json(route,{error:{status:404,message:`Unhandled fixture ${method} ${pathname}`}},404);
    });

    await page.getByRole("button", {name:"Options",exact:true}).click();
    await page.getByText(/Spotify…/).first().click();
    const window = page.locator(".uno-classicwindowframe").filter({hasText:"Close Spotify"});
    await window.waitFor({state:"visible"});
    const button = label => window.locator("div.uno-button").filter({hasText:new RegExp("^" + label + "$")});
    const client = window.locator(".uno-textbox").filter({hasText:"Spotify app client ID"}).locator("textarea");
    await client.fill(clientId);
    await button("Sign in with Spotify").click();
    await window.getByText("UnoPlay Tester",{exact:true}).waitFor({timeout:90000});
    await window.getByText("Studio collection · UnoPlay Tester · 51 items",{exact:true}).waitFor();
    await page.waitForFunction(() => window.spotifyFixturePlayer != null);
    assert.equal(new URL(page.url()).searchParams.has("code"), false, "OAuth codes are removed from browser history");
    assert.deepEqual(grants, ["authorization_code", "refresh_token"], "One 401 triggers exactly one refresh");
    assert.equal(await page.evaluate(() => sessionStorage.getItem("unoplay.spotify.pending")), null, "PKCE request is one-use");
    assert.ok(await page.evaluate(() => sessionStorage.getItem("unoplay.spotify.token")), "Tab-scoped session restored after redirect");
    assert.equal(await page.evaluate(() => Object.values(localStorage).some(value => value.includes("fixture-access") || value.includes("fixture-refresh"))), false);

    await window.getByText("Studio collection · UnoPlay Tester · 51 items",{exact:true}).dblclick();
    await window.getByText("1. Fixture song · Fixture artist · 3:00",{exact:true}).waitFor();
    await button("Next page").click();
    await window.getByText("51. Final fixture track · Fixture artist · 3:00",{exact:true}).waitFor();
    await button("Play selected").click();
    await button("Pause").waitFor();
    assert.ok(requests.some(request => request.pathname === "/v1/me/player/play" && request.body.context_uri === playlist.uri && request.body.offset.position === 50));
    await button("Pause").click();
    await button("Play").waitFor();
    assert.ok(await page.evaluate(() => spotifyFixtureCalls.some(call => call[0] === "pause")));
    await button("Play").click();
    await button("Pause").waitFor();

    await button("Liked songs").click();
    await window.getByText("1. Fixture song · Fixture artist · 3:00",{exact:true}).waitFor();
    await button("Item actions").click();
    await page.getByText("Add to playback queue",{exact:true}).click();
    await page.waitForTimeout(200);
    assert.ok(requests.some(request => request.pathname === "/v1/me/player/queue" && request.method === "POST" && request.query.uri === track.uri));
    await window.locator("textarea:visible").fill("Fixture");
    await button("Search").click();
    await window.getByText("Tracks matching “Fixture”",{exact:true}).waitFor();
    assert.ok(requests.some(request => request.pathname === "/v1/search" && request.query.limit === "10"));
    await button("New playlist").click();
    const dialog = page.locator(".uno-contentdialog").filter({hasText:"Create Spotify playlist"});
    await dialog.waitFor();
    await dialog.locator("textarea").first().fill("Created in UnoPlay");
    await dialog.locator("div.uno-button").filter({hasText:/^Create$/}).click();
    await window.getByText("Created in UnoPlay",{exact:true}).waitFor();
    assert.ok(requests.some(request => request.pathname === "/v1/me/playlists" && request.method === "POST" && request.body.name === "Created in UnoPlay" && request.body.public === false));

    await button("Sign out").click();
    await button("Sign in with Spotify").waitFor();
    assert.equal(await page.evaluate(() => sessionStorage.getItem("unoplay.spotify.token")), null);
    assert.ok(await page.evaluate(() => spotifyFixtureCalls.some(call => call[0] === "disconnect")));
    authorizationOutcome = "denied";
    await button("Sign in with Spotify").click();
    await window.getByText("Spotify sign-in was cancelled.", {exact:true}).waitFor({timeout:90000});
    assert.equal(grants.length, 2, "Consent denial must not exchange a token");
    authorizationOutcome = "mismatch";
    await button("Sign in with Spotify").click();
    await window.getByText("Spotify sign-in state did not match. Start sign-in again.", {exact:true}).waitFor({timeout:90000});
    assert.equal(grants.length, 2, "Mismatched OAuth state must not exchange a token");
    assert.equal(new URL(page.url()).searchParams.has("code"), false);
    await page.reload();
    await page.getByRole("button",{name:"Play",exact:true}).waitFor({timeout:90000});
    await page.getByRole("button",{name:"Play",exact:true}).click();
    await page.waitForFunction(() => [...document.querySelectorAll("audio,video")].some(media => !media.paused));
    console.log("Spotify published UI passed PKCE, history cleanup, automatic refresh, playlist pagination/context offsets, SDK transport, search, queue, playlist creation, logout, denied/mismatched callbacks and local-playback recovery using explicit fixtures.");
}

module.exports = { verifySpotify };
