// Keep the archive as binary data in IndexedDB. No copies travel through localStorage
// or the virtual filesystem; a single read/write transaction changes the selected skin.
globalThis.UnoPlayStorage = (() => {
    let connection;
    function database() {
        if (connection) return connection;
        connection = new Promise((resolve, reject) => {
            const open = indexedDB.open("unoplay-media-settings", 1);
            open.onupgradeneeded = () => open.result.createObjectStore("skins");
            open.onsuccess = () => {
                const db = open.result;
                db.onversionchange = () => { db.close(); connection = undefined; };
                resolve(db);
            };
            open.onerror = () => { connection = undefined; reject(open.error); };
        });
        return connection;
    }
    async function access(mode, request) {
        const db = await database();
        return new Promise((resolve, reject) => {
            const tx = db.transaction("skins", mode);
            const op = request(tx.objectStore("skins"));
            tx.oncomplete = () => resolve(op.result);
            tx.onabort = tx.onerror = () => reject(tx.error || op.error);
        });
    }
    return {
        bytes: value => new Uint8Array(value),
        load: async () => (await access("readonly", s => s.get("current"))) || new Uint8Array(),
        save: async bytes => { await access("readwrite", s => s.put(bytes, "current")); },
        clear: async () => { await access("readwrite", s => s.delete("current")); }
    };
})();
