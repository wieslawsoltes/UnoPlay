const assert = require("node:assert/strict");

const documentPath = "/UnoPlay/tests/embedded-document.html";
const documentBody = "<!doctype html><html><title>Local MiniBrowser fixture</title><body><h1>MiniBrowser navigation verified</h1></body></html>";

async function verifyToolWindows(page, appUrl) {
    const windows = page.locator(".uno-classicwindowframe");
    async function open(label, closeName) {
        await page.getByRole("button", { name: "Options", exact: true }).click();
        await page.getByText(label).first().click();
        const window = windows.filter({ hasText: closeName });
        await window.waitFor({ state: "visible" });
        return window;
    }
    async function close(window, name) {
        const button = window.locator("div.uno-button").filter({ hasText: name }).first();
        const rect = await button.boundingBox();
        assert.ok(rect, "window close button");
        await page.mouse.click(rect.x + rect.width / 2, rect.y + rect.height / 2);
        await window.waitFor({ state: "hidden" });
    }

    const library = await open(/Media library…/, "Close Media Library");
    const search = library.locator("textarea");
    await search.fill("no-recording-matches-this");
    await library.getByText("No matching tracks", { exact: true }).waitFor();
    await search.fill("first");
    await library.getByText(/1\. UnoPlay.*First Light/).first().waitFor();
    await close(library, /^Close Media Library$/);

    const mini = await open(/MiniBrowser…/, "Close MiniBrowser");
    assert.equal(await mini.locator("iframe").count(), 0, "no remote load before an explicit address");
    const address = mini.locator("textarea");
    await address.click();
    await address.fill(new URL(documentPath, appUrl).href);
    await address.press("Enter");
    await mini.frameLocator("iframe").getByRole("heading", { name: "MiniBrowser navigation verified" }).waitFor();
    await close(mini, /^Close MiniBrowser$/);
    await open(/MiniBrowser…/, "Close MiniBrowser");
    await address.press("Enter");
    await mini.frameLocator("iframe").getByRole("heading", { name: "MiniBrowser navigation verified" }).waitFor();
    const menu = await mini.locator("div.uno-button").filter({hasText:/^Menu$/}).boundingBox();
    assert.ok(menu, "MiniBrowser menu button");
    await page.mouse.click(menu.x + menu.width / 2, menu.y + menu.height / 2);
    await page.locator(".uno-menuflyoutitem").filter({hasText:/^Close MiniBrowser$/}).click();
    await mini.waitFor({state:"hidden"});
    await page.waitForFunction(window => [...window.querySelectorAll("iframe")]
        .every(frame => frame.getAttribute("src") === "about:blank"), await mini.elementHandle());
    await page.keyboard.press("Space");
    await page.waitForFunction(() => [...document.querySelectorAll("audio,video")].some(media => !media.paused));
    await page.keyboard.press("Space");
    await page.waitForFunction(() => [...document.querySelectorAll("audio,video")].every(media => media.paused));
    console.log("Skinned media library filtering, both window close paths, restored transport shortcuts, and explicit MiniBrowser navigation passed.");
}

module.exports = { documentPath, documentBody, verifyToolWindows };
