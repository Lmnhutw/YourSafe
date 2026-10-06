"use strict";

// Uses an existing Playwright installation; no package download or test framework.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const http = require("node:http");
const { chromium } = require("playwright");

const root = __dirname;
const mime = {
  ".html": "text/html; charset=utf-8",
  ".css": "text/css",
  ".js": "text/javascript",
  ".png": "image/png",
  ".svg": "image/svg+xml",
  ".ttf": "font/ttf",
};
const server = http.createServer((request, response) => {
  const pathname = new URL(request.url, "http://localhost").pathname;
  const relative =
    pathname.replace(/^\/PasswordTool\.Core\//, "/").replace(/^\//, "") ||
    "index.html";
  const file = path.resolve(root, relative);
  if (
    !file.startsWith(root + path.sep) ||
    !fs.existsSync(file) ||
    !fs.statSync(file).isFile()
  ) {
    response.writeHead(404).end();
    return;
  }
  response.setHeader(
    "Content-Type",
    mime[path.extname(file)] || "application/octet-stream",
  );
  fs.createReadStream(file).pipe(response);
});

(async () => {
  let browser;
  try {
    await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
    const base = `http://127.0.0.1:${server.address().port}/PasswordTool.Core/`;
    browser = await chromium.launch({
      headless: true,
      channel: process.platform === "win32" ? "msedge" : undefined,
    });
    const errors = [];
    const context = await browser.newContext({ reducedMotion: "reduce" });
    const page = await context.newPage();
    page.on("pageerror", (error) => errors.push(error.message));
    page.on("response", (response) => {
      if (response.status() >= 400)
        errors.push(`${response.status()} ${response.url()}`);
    });
    fs.mkdirSync(path.join(root, "../.impeccable/review"), { recursive: true });
    for (const width of [1440, 768, 390, 320]) {
      await page.setViewportSize({ width, height: 900 });
      await page.goto(base);
      await page.evaluate(() => document.fonts.ready);
      assert.equal(await page.locator("h1").textContent(), "YourSafe.");
      assert.equal(
        await page.evaluate(
          () => document.documentElement.scrollWidth <= innerWidth,
        ),
        true,
        `overflow at ${width}`,
      );
      await page.locator(".app-figure img").scrollIntoViewIfNeeded();
      await page.waitForFunction(() =>
        [...document.images].every(
          (img) => img.complete && img.naturalWidth > 0,
        ),
      );
      await page.evaluate(() => scrollTo(0, 0));
      for (const href of await page
        .locator('a[href^="#"]')
        .evaluateAll((links) =>
          links.map((link) => link.getAttribute("href")),
        )) {
        assert.equal(await page.locator(href).count(), 1, `anchor ${href}`);
      }
      assert.equal(
        await page
          .locator(
            'a[href="https://github.com/Lmnhutw/PasswordTool.Core/releases"]',
          )
          .count(),
        3,
      );
      assert.equal(
        await page.locator('.brand-logo[src="./assets/app-logo.png"]').count(),
        2,
      );
      assert.equal(
        await page.locator('link[rel="icon"]').getAttribute("href"),
        "./assets/app-logo.png",
      );
      if (width <= 680) {
        await page.getByRole("button", { name: "Menu" }).click();
        assert.equal(
          await page.locator(".menu-toggle").getAttribute("aria-expanded"),
          "true",
        );
        await page.keyboard.press("Escape");
        assert.equal(
          await page.locator(".menu-toggle").getAttribute("aria-expanded"),
          "false",
        );
        assert.equal(
          await page
            .locator(".menu-toggle")
            .evaluate((button) => button === document.activeElement),
          true,
        );
        await page.locator(".menu-toggle").click();
        await page
          .locator("#site-nav")
          .getByText("Tính năng", { exact: true })
          .click();
        assert.equal(
          await page.locator(".menu-toggle").getAttribute("aria-expanded"),
          "false",
        );
      } else {
        assert.equal(await page.locator(".menu-toggle").isVisible(), false);
        assert.equal(await page.locator("#site-nav").isVisible(), true);
      }
      await page.locator("#faq summary").first().click();
      assert.equal(
        await page.locator("#faq details").first().getAttribute("open"),
        "",
      );
      await page.locator("#faq summary").first().click();
      await page.evaluate(() => scrollTo(0, 0));
      assert.equal(
        await page
          .locator(".hero-copy")
          .evaluate((element) => getComputedStyle(element).animationName),
        "none",
      );
      if ([1440, 390].includes(width)) {
        await page.screenshot({
          path: path.join(
            root,
            `../.impeccable/review/${width === 1440 ? "desktop" : "mobile"}.png`,
          ),
          fullPage: true,
        });
      }
    }
    await page.locator("#install summary").click();
    await page.evaluate(() => {
      window.copiedCommand = "";
      Object.defineProperty(navigator.clipboard, "writeText", {
        configurable: true,
        value: async (text) => {
          window.copiedCommand = text;
        },
      });
    });
    await page.locator(".copy-button").click();
    assert.equal(
      await page.evaluate(() => window.copiedCommand),
      "Get-FileHash .\\TEN-GOI-TAI.zip -Algorithm SHA256",
    );
    await page.evaluate(() =>
      Object.defineProperty(navigator.clipboard, "writeText", {
        value: async () => {
          throw new Error("denied");
        },
      }),
    );
    await page.locator(".copy-button").click();
    assert.match(
      await page.locator(".copy-status").textContent(),
      /Không sao chép/,
    );
    const noJs = await browser.newContext({
      javaScriptEnabled: false,
      viewport: { width: 390, height: 844 },
    });
    const plain = await noJs.newPage();
    await plain.goto(base);
    assert.equal(await plain.locator("#site-nav").isVisible(), true);
    assert.equal(await plain.locator(".menu-toggle").isVisible(), false);
    assert.equal(await plain.locator(".button-primary").isVisible(), true);
    await plain.locator("summary").first().click();
    assert.equal(
      await plain.locator("details").first().getAttribute("open"),
      "",
    );
    assert.equal(
      await plain.evaluate(
        () => document.documentElement.scrollWidth <= innerWidth,
      ),
      true,
    );
    const animatedContext = await browser.newContext({
      viewport: { width: 1440, height: 900 },
      reducedMotion: "no-preference",
    });
    const animated = await animatedContext.newPage();
    await animated.goto(base);
    await animated.locator(".feature-row").first().scrollIntoViewIfNeeded();
    await animated.waitForFunction(() =>
      document.querySelector(".feature-row").classList.contains("is-revealed"),
    );
    assert.equal(
      await animated.locator(".feature-row").first().isVisible(),
      true,
    );
    assert.equal(
      await animated.evaluate(() =>
        document.fonts.check('700 24px "Be Vietnam Pro"'),
      ),
      true,
    );
    assert.deepEqual(errors, []);
    console.log(
      "PASS: desktop/tablet/mobile/narrow, assets/base path, app logo/favicon, menu, FAQ, clipboard, reveal, reduced motion and no-JS.",
    );
  } finally {
    if (browser) await browser.close();
    await new Promise((resolve) => server.close(resolve));
  }
})().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
