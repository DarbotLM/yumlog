import { chromium } from "playwright-core";
import { strict as assert } from "node:assert";

const browser = await chromium.connectOverCDP("http://127.0.0.1:47619");
// The DevTools endpoint can list the Studio target a moment before Playwright attaches it, so poll every context briefly.
let page;
for (let attempt = 0; attempt < 60 && !page; attempt++) {
  page = browser.contexts().flatMap(item => item.pages()).find(item => item.url().includes("yumlog-studio.local"));
  if (!page) await new Promise(resolve => setTimeout(resolve, 250));
}
assert.ok(page, "Could not find the native studio WebView");
page.setDefaultTimeout(8000);
const errors = [];
page.on("pageerror", error => errors.push(error.message));
page.on("requestfailed", item => {
  const detail = item.failure()?.errorText;
  if (detail !== "net::ERR_ABORTED") console.log(item.url(), detail);
});
await page.getByRole("heading", { name: "Capture Review", exact: true }).first().waitFor();
const labels = ["Video", "Screenshot", "Audio", "Transcript", "Agents", "Voices", "Insights", "Library", "Harvest", "Settings"];
assert.deepEqual((await page.getByRole("tab").locator("span").allTextContents()), labels);
const go = label => page.getByRole("tab", { name: label, exact: true }).click();
const video = page.locator("#player");
await page.waitForFunction(() => document.querySelector("#player")?.duration === 6, { timeout: 8000 });
await video.evaluate(async player => {
  player.muted = true;
  await player.play();
});
await page.waitForFunction(() => document.querySelector("#player").currentTime > 0.3);
await video.evaluate(player => player.pause());
console.log("Native video playback and decode passed");

await go("Screenshot");
assert.equal(await page.locator(".frame-tile").count(), 4);
await page.waitForFunction(() => [...document.querySelectorAll(".frame-tile img")].every(img => img.complete && img.naturalWidth > 0));
await page.locator(".frame-tile").first().click();
await page.getByRole("button", { name: "Save a copy", exact: true }).waitFor();
await go("Audio");
await page.waitForFunction(() => document.querySelector("audio")?.duration > 5);
await page.getByAltText("Spectrogram showing audio frequencies over time").waitFor();
await go("Transcript");
await page.getByRole("button", { name: "View transcript", exact: true }).click();
await page.getByRole("button", { name: /Compare scenes across the recording/ }).click();
await page.waitForFunction(() => Math.abs(document.querySelector("#player").currentTime - 2) < 0.1);
await go("Agents");
await page.getByRole("button", { name: "Load capability catalog", exact: true }).click();
await page.locator(".capability").first().waitFor();
await go("Voices");
await page.getByRole("button", { name: "List installed voices", exact: true }).click();
await page.getByLabel("Installed Windows voice").waitFor();
await go("Insights");
await page.getByText("Frame size", { exact: true }).waitFor();
await go("Harvest");
await page.getByRole("button", { name: "Build Paperboy bundle", exact: true }).waitFor();
await page.locator("#flip").click();
await page.locator("#card[data-flipped=true]").waitFor();
await page.getByText("Harvest and delivery", { exact: true }).waitFor();
assert.equal(await page.locator("#tool-face").evaluate(node => node.inert), true);
await page.getByText("Full card JSON", { exact: true }).click();
const card = JSON.parse(await page.locator("#paperboy-face .code").textContent());
assert.equal(card.role, "video-workspace");
assert.equal(card.flipcard.tabs.length, labels.length);
await go("Settings");
await page.getByText("Workspace settings", { exact: true }).waitFor();
await page.locator("#flip").click();
await page.getByLabel("Output directory").waitFor();
const size = page.getByLabel("Card size");
await size.selectOption("compact");
await page.locator("#card[data-size=compact]").waitFor();
await size.selectOption("wide");
await page.locator("#card[data-size=wide]").waitFor();
await go("Video");
await page.locator("#tab-video").focus();
await page.keyboard.press("ArrowRight");
assert.equal(await page.locator("#tab-screenshot").getAttribute("aria-selected"), "true");
await page.keyboard.press("End");
assert.equal(await page.locator("#tab-settings").getAttribute("aria-selected"), "true");
await go("Video");
await page.locator("#card").scrollIntoViewIfNeeded();
await page.evaluate(() => window.scrollTo(0, 0));
await page.screenshot({ path: process.argv[2], fullPage: true, animations: "disabled" });

await page.setViewportSize({ width: 480, height: 760 });
await page.getByRole("button", { name: "Toggle library", exact: true }).click();
await page.getByLabel("Find a recording").fill("nonsense");
await page.getByText("No matching recordings.", { exact: true }).waitFor();
await page.getByRole("button", { name: "Toggle library", exact: true }).click();
const geometry = await page.locator("#card").evaluate(node => ({
  width: node.getBoundingClientRect().width,
  screen: document.documentElement.clientWidth,
  overflow: document.documentElement.scrollWidth,
  layout: getComputedStyle(node.querySelector(".card-body")).display
}));
assert.ok(geometry.width <= geometry.screen && geometry.overflow === geometry.screen);
assert.equal(geometry.layout, "block");
assert.deepEqual(errors, []);
console.log("All Studio tabs, keyboard navigation, Paperboy flip, native media, and adaptive layout passed");
await browser.close();
