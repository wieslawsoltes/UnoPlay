// Use the platform cursor so the pointer moves independently of the .NET render loop.
// ANI files carry CUR or ICO frames in RIFF chunks. Their rates are in sixtieths of a second.
(() => {
    "use strict";
    let next = 1, active = 0, owner = 0, timer = 0, step = 0, target, lastPointerTarget, style;
    const registered = new Map();
    const tag = (b, p) => String.fromCharCode(b[p], b[p + 1], b[p + 2], b[p + 3]);

    function frames(bytes) {
        const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
        const u32 = p => view.getUint32(p, true);
        if (bytes.length < 6) return null;
        if (tag(bytes, 0) !== "RIFF") return {images: [bytes], order: [0], delays: [0]};
        if (bytes.length < 12 || tag(bytes, 8) !== "ACON") return null;
        const images = [];
        let rate = 6, steps = 0, order, rates;
        function chunks(start, end, framesOnly) {
            while (start + 8 <= end) {
                const type = tag(bytes, start), length = u32(start + 4), p = start + 8;
                if (p + length > end) throw new RangeError("Truncated ANI chunk");
                if (framesOnly) {
                    if (type === "icon" && images.length < 256) images.push(bytes.subarray(p, p + length));
                } else if (type === "anih" && length >= 36) {
                    steps = u32(p + 8); rate = u32(p + 28);
                } else if (type === "seq " || type === "rate") {
                    const values = Array.from({length: Math.min(4096, length >>> 2)}, (_, i) => u32(p + 4 * i));
                    if (type === "seq ") order = values; else rates = values;
                } else if (type === "LIST" && length >= 4 && tag(bytes, p) === "fram") {
                    chunks(p + 4, p + length, true);
                }
                start = p + length + (length & 1);
            }
        }
        chunks(12, Math.min(bytes.length, 8 + u32(4)), false);
        if (!images.length || steps > 4096) return null;
        order ??= Array.from({length: steps || images.length}, (_, i) => i % images.length);
        if (!order.length || order.some(i => i >= images.length)) return null;
        return {images, order, delays: order.map((_, i) => Math.max(16, (rates?.[i] || rate || 6) * 1000 / 60))};
    }

    function releaseTarget() {
        if (!target) return;
        target.classList.remove("unoplay-classic-pointer");
        target.style.removeProperty("--unoplay-classic-pointer");
        target = undefined;
    }
    function clear() {
        clearTimeout(timer);
        active = owner = timer = step = 0;
        releaseTarget();
    }
    function update() {
        const cursor = registered.get(active);
        if (cursor && target) target.style.setProperty("--unoplay-classic-pointer", cursor.frames[cursor.order[step]]);
    }
    function tick() {
        const cursor = registered.get(active);
        if (!cursor || cursor.order.length < 2) return;
        timer = setTimeout(() => {
            step = (step + 1) % cursor.order.length;
            update();
            tick();
        }, cursor.delays[step]);
    }
    function pointerEvent(e) { if (e.pointerType === "mouse") lastPointerTarget = e.target; }
    document.addEventListener("pointerover", pointerEvent, true);
    document.addEventListener("pointermove", pointerEvent, true);
    globalThis.UnoPlaySkinCursors = {
        register(input) {
            const bytes = new Uint8Array(input);
            let parsed;
            try { parsed = frames(bytes); } catch { return 0; }
            if (!parsed) return 0;
            const refs = [];
            for (const b of parsed.images) {
                if (b.length < 22 || b[0] || b[1] || b[3] || ![1,2].includes(b[2]) || !(b[4] || b[5])) {
                    for (const url of refs) URL.revokeObjectURL(url);
                    return 0;
                }
                refs.push(URL.createObjectURL(new Blob([b], {type: "image/x-icon"})));
            }
            // Native CUR decoding preserves embedded hotspots. ICOs have no embedded hotspot.
            const id = next++;
            registered.set(id, {...parsed, images: undefined, urls: refs, frames: refs.map(url => 'url("' + url + '"), auto')});
            return id;
        },
        show(instance, id) {
            if (!registered.has(id) || !(lastPointerTarget instanceof HTMLElement || lastPointerTarget instanceof SVGElement)) {
                if (owner === instance) clear();
                return;
            }
            if (!style) {
                style = document.createElement("style");
                style.textContent = ".unoplay-classic-pointer { cursor: var(--unoplay-classic-pointer) !important; }";
                document.head.appendChild(style);
            }
            const changed = active !== id || owner !== instance;
            if (changed) { clearTimeout(timer); active = id; owner = instance; step = 0; }
            if (target !== lastPointerTarget) {
                releaseTarget(); target = lastPointerTarget;
                target.classList.add("unoplay-classic-pointer");
            }
            update();
            if (changed) tick();
        },
        hide(instance) { if (owner === instance) clear(); },
        release(id) {
            if (active === id) clear();
            const cursor = registered.get(id);
            if (cursor) for (const url of cursor.urls) URL.revokeObjectURL(url);
            registered.delete(id);
        }
    };
})();
