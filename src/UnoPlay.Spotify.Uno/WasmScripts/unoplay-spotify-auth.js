// Authorization state belongs to this browser tab. Client IDs are public app configuration.
(() => {
    "use strict";
    const prefix = "unoplay.spotify.";
    globalThis.UnoPlaySpotifyAuth = Object.freeze({
        redirectUri() {
            const url = new URL(document.baseURI);
            url.search = "";
            url.hash = "";
            return url.href;
        },
        callback() {
            const url = new URL(location.href);
            return url.searchParams.has("state") && (url.searchParams.has("code") || url.searchParams.has("error"))
                ? url.href : "";
        },
        clearCallback() {
            const url = new URL(location.href);
            for (const name of ["code", "state", "error", "error_description"])
                url.searchParams.delete(name);
            history.replaceState(history.state, "", url.href);
        },
        read(key) { return sessionStorage.getItem(prefix + key) || ""; },
        write(key, value) {
            if (value) sessionStorage.setItem(prefix + key, value);
            else sessionStorage.removeItem(prefix + key);
        },
        authorize(url) {
            const target = new URL(url);
            if (target.origin !== "https://accounts.spotify.com" || target.pathname !== "/authorize")
                throw new Error("Invalid Spotify authorization address.");
            location.assign(target.href);
        }
    });
})();
