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

async function assertSlide(page, index) {
  const state = await page.locator(".showcase").evaluate((showcase) => ({
    slides: [...showcase.querySelectorAll(".showcase-slide")].map((slide) => ({ hidden: slide.hidden, inert: slide.inert })),
    current: [...showcase.querySelectorAll("[data-slide]")].map((button) => button.getAttribute("aria-current")),
  }));
  assert.deepEqual(state.slides, [0, 1, 2].map((position) => ({ hidden: position !== index, inert: position !== index })));
  assert.deepEqual(state.current, [0, 1, 2].map((position) => String(position === index)));
}

async function assertImage(image) {
  const state = await image.evaluate((img) => {
    const bounds = img.getBoundingClientRect();
    const style = getComputedStyle(img);
    return { width: bounds.width, height: bounds.height, naturalWidth: img.naturalWidth, naturalHeight: img.naturalHeight, declaredWidth: Number(img.getAttribute("width")), declaredHeight: Number(img.getAttribute("height")), radius: parseFloat(style.borderTopLeftRadius), fit: style.objectFit, alt: img.alt };
  });
  assert.ok(state.naturalWidth > 0 && state.width > 0, "product image loaded and visible");
  assert.equal(state.declaredWidth, state.naturalWidth, "declared image width");
  assert.equal(state.declaredHeight, state.naturalHeight, "declared image height");
  assert.ok(state.fit === 'contain' || Math.abs(state.width / state.height - state.naturalWidth / state.naturalHeight) < 0.005, "native image aspect ratio or contained image in a fixed gallery stage");
  assert.ok(state.radius >= 8, "image itself has rounded edges");
  assert.notEqual(state.fit, "cover", "image is not cropped");
  assert.ok(state.alt.length > 20, "descriptive image alternative");
}

async function swipe(page, start, end, cancel = false) {
  await page.locator(".showcase-slides").evaluate((stage, { start, end, cancel }) => {
    stage.dispatchEvent(new PointerEvent("pointerdown", { bubbles: true, pointerType: "touch", isPrimary: true, clientX: start[0], clientY: start[1] }));
    stage.dispatchEvent(new PointerEvent(cancel ? "pointercancel" : "pointerup", { bubbles: true, pointerType: "touch", isPrimary: true, clientX: end[0], clientY: end[1] }));
  }, { start, end, cancel });
}

async function verifyDemo(browser, base) {
  const { buildSync } = require('../browser-extension/node_modules/esbuild');
  const extensionFields = buildSync({ entryPoints: [path.join(root, '../browser-extension/src/fields.ts')],
    bundle: true, write: false, format: 'iife', globalName: 'YourSafeFields' }).outputFiles[0].text;
  const context = await browser.newContext({ reducedMotion: 'reduce' });
  await context.route(`${base}extension-fields.js`, route => route.fulfill({ contentType: 'text/javascript', body: extensionFields }));
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('response', response => { if (response.status() >= 400) errors.push(`${response.status()} ${response.url()}`); });
  await page.goto(base);
  await page.locator('.closing-demo a').click();
  assert.equal(page.url(), `${base}demo-login.html`);
  for (const width of [1440, 768, 390, 320]) {
    await page.setViewportSize({ width, height: 900 });
    await page.goto(`${base}demo-login.html`);
    await page.evaluate(() => document.fonts.ready);
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true, `demo overflow at ${width}`);
    assert.equal(await page.getByLabel('Tên đăng nhập').inputValue(), '');
    assert.equal(await page.getByLabel('Mật khẩu', { exact: true }).inputValue(), '');
    assert.equal(await page.getByLabel('Mật khẩu', { exact: true }).getAttribute('type'), 'password');
    const panel = await page.locator('.demo-panel').boundingBox();
    const guide = await page.locator('.demo-guide').boundingBox();
    assert.ok(width <= 680 ? panel.y < guide.y : guide.x < panel.x, `demo reading layout at ${width}`);
    if ([1440, 390].includes(width)) await page.screenshot({ path: path.join(root, `../.impeccable/review/demo-${width}.png`), fullPage: true });
  }
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto(`${base}demo-login.html`);
  await page.addScriptTag({ url: `${base}extension-fields.js` });
  await page.evaluate(() => document.fonts.ready);
  const requests = [];
  page.on('request', request => requests.push({ url: request.url(), body: request.postData() }));
  // Exercise the extension's actual visibility, form-origin and field discovery/fill code.
  assert.equal(await page.evaluate(async () => {
    const fields = YourSafeFields.findFields();
    if (!fields || fields.username?.id !== 'demo-username' || fields.password.id !== 'demo-password') return false;
    await YourSafeFields.fillFields(fields, 'alex.demo@example.test', 'Demo-only-password!');
    return true;
  }), true);
  assert.equal(await page.getByLabel('Tên đăng nhập').inputValue(), 'alex.demo@example.test');
  assert.equal(await page.getByLabel('Mật khẩu', { exact: true }).inputValue(), 'Demo-only-password!');
  assert.equal(await page.locator('#login-success').isVisible(), false, 'fill never submits');
  await page.getByLabel('Mật khẩu', { exact: true }).press('Enter');
  assert.equal(await page.locator('#login-success').isVisible(), true);
  assert.equal(await page.locator('#demo-status').textContent(), 'Đăng nhập thử thành công');
  assert.equal(await page.locator('#success-title').evaluate(element => element === document.activeElement), true);
  await page.screenshot({ path: path.join(root, '../.impeccable/review/demo-success.png'), fullPage: true });
  await page.getByRole('button', { name: 'Thử lại', exact: true }).click();
  assert.equal(await page.getByLabel('Tên đăng nhập').inputValue(), '');
  assert.equal(await page.getByLabel('Mật khẩu', { exact: true }).inputValue(), '');
  assert.equal(await page.getByLabel('Tên đăng nhập').evaluate(element => element === document.activeElement), true);
  for (const [username, password] of [['not-an-email <demo>', 'x'], ['', '']]) {
    await page.getByLabel('Tên đăng nhập').fill(username);
    await page.getByLabel('Mật khẩu', { exact: true }).fill(password);
    await page.getByRole('button', { name: 'Đăng nhập', exact: true }).click();
    assert.equal(await page.locator('#login-success').isVisible(), true, 'all demo values accepted');
    await page.getByRole('button', { name: 'Thử lại', exact: true }).click();
  }
  assert.deepEqual(requests, [], 'filling, submit and reset never send credentials or navigate');
  assert.equal(page.url(), `${base}demo-login.html`);
  assert.deepEqual(await page.evaluate(() => ({ local: localStorage.length, session: sessionStorage.length, cookies: document.cookie })), { local: 0, session: 0, cookies: '' });
  await page.evaluate(() => Object.defineProperty(navigator.clipboard, 'writeText', { configurable: true, value: async text => { window.copiedAddress = text; } }));
  await page.getByRole('button', { name: 'Sao chép', exact: true }).click();
  assert.equal(await page.evaluate(() => window.copiedAddress), `${base}demo-login.html`);
  await page.evaluate(() => Object.defineProperty(navigator.clipboard, 'writeText', { value: async () => { throw new Error('denied'); } }));
  await page.getByRole('button', { name: 'Sao chép', exact: true }).click();
  assert.match(await page.locator('#address-status').textContent(), /Không sao chép được/);
  await page.getByLabel('Tên đăng nhập').fill('temporary-demo');
  await page.getByRole('link', { name: 'Về trang chủ' }).click();
  assert.equal(page.url(), `${base}index.html`);
  await page.goBack();
  assert.equal(await page.getByLabel('Tên đăng nhập').inputValue(), '', 'leaving the page clears inputs');
  await page.goto(new URL('/demo-login.html', base).href);
  assert.equal(await page.locator('#demo-address').textContent(), new URL('/demo-login.html', base).href);
  assert.deepEqual(errors, []);
  await context.close();
  const noJs = await browser.newContext({ javaScriptEnabled: false });
  const plain = await noJs.newPage();
  await plain.goto(`${base}demo-login.html`);
  assert.equal(await plain.getByRole('button', { name: 'Đăng nhập', exact: true }).isDisabled(), true);
  assert.equal(await plain.getByLabel('Tên đăng nhập').isDisabled(), true, 'no-JS cannot submit secrets');
  await noJs.close();
  const blockedJs = await browser.newContext();
  await blockedJs.route('**/demo-login.js', route => route.abort());
  const blocked = await blockedJs.newPage();
  await blocked.goto(`${base}demo-login.html`);
  assert.equal(await blocked.getByLabel('Tên đăng nhập').isDisabled(), true, 'missing script cannot submit secrets');
  await blockedJs.close();
  console.log('PASS: demo CTA/base paths, 1440/768/390/320 layouts, actual extension field detection/fill, keyboard submit, arbitrary/empty input, reset/focus, no secret requests/storage, copy success/failure and no-JS/script-failure safety.');
}

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
    const layoutScores = [];
    const context = await browser.newContext({ reducedMotion: "reduce" });
    await context.addInitScript(() => {
      window.layoutShiftScore = 0;
      window.layoutShifts = [];
      new PerformanceObserver((entries) => {
        for (const entry of entries.getEntries()) {
          if (!entry.hadRecentInput) {
            window.layoutShiftScore += entry.value;
            window.layoutShifts.push({ value: entry.value, sources: entry.sources.map((source) => ({ node: source.node?.className, before: source.previousRect.toJSON(), after: source.currentRect.toJSON() })) });
          }
        }
      }).observe({ type: "layout-shift", buffered: true });
    });
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
      const showcase = page.locator(".showcase");
      const initialBounds = await showcase.boundingBox();
      const initialDocumentY = initialBounds.y + await page.evaluate(() => scrollY);
      assert.equal(await showcase.getAttribute("role"), "region");
      assert.equal(await showcase.getAttribute("tabindex"), "0");
      await assertSlide(page, 0);
      for (const index of [1, 2, 0]) {
        await page.locator(`[data-slide="${index}"]`).click();
        await assertSlide(page, index);
        const active = page.locator(".showcase-slide").nth(index);
        const image = active.locator(".product-image");
        if (await image.count()) {
          await image.evaluate((img) => img.decode());
          await assertImage(image);
          const imageBounds = await image.boundingBox();
          const captionBounds = await active.locator(".showcase-footer").boundingBox();
          assert.ok(captionBounds.y >= imageBounds.y + imageBounds.height + 4, `showcase image and caption do not overlap at ${width}`);
        }
        assert.equal(await active.evaluate((article) => getComputedStyle(article).animationName), "none", "reduced-motion carousel");
        const bounds = await showcase.boundingBox();
        const documentY = bounds.y + await page.evaluate(() => scrollY);
        assert.ok(Math.abs(bounds.height - initialBounds.height) < 1 && Math.abs(documentY - initialDocumentY) < 1, `no gallery layout shift at ${width}`);
        if ([1440, 390].includes(width) && index > 0) {
          await showcase.screenshot({ path: path.join(root, `../.impeccable/review/showcase-${width}-${index}.png`) });
        }
      }
      await showcase.focus();
      for (const [key, index] of [["ArrowLeft", 2], ["ArrowRight", 0], ["End", 2], ["Home", 0]]) {
        await page.keyboard.press(key);
        await assertSlide(page, index);
      }
      await page.locator('[data-direction="-1"]').click();
      await assertSlide(page, 2);
      await page.locator('[data-direction="1"]').click();
      await assertSlide(page, 0);
      await swipe(page, [200, 100], [100, 110]);
      await assertSlide(page, 1);
      assert.equal(await page.locator("#showcase-desktop .showcase-link").evaluate((link) => {
        let prevented;
        link.addEventListener("click", (event) => { prevented = event.defaultPrevented; event.preventDefault(); }, { once: true });
        link.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true, detail: 0 }));
        return prevented;
      }), false, "keyboard activation after swipe permits image navigation");
      assert.equal(await page.locator("#showcase-desktop .showcase-link").evaluate((link) => link.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true, detail: 1 }))), false, "accepted swipe suppresses image navigation");
      await swipe(page, [100, 100], [100, 100]);
      assert.equal(await page.locator("#showcase-desktop .showcase-link").evaluate((link) => {
        let prevented;
        link.addEventListener("click", (event) => { prevented = event.defaultPrevented; event.preventDefault(); }, { once: true });
        link.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true }));
        return prevented;
      }), false, "normal tap permits image navigation");
      await swipe(page, [100, 100], [200, 110]);
      await assertSlide(page, 0);
      for (const [start, end, cancel] of [[[200, 100], [160, 100], false], [[200, 100], [100, 200], false], [[200, 100], [100, 100], true]]) {
        await swipe(page, start, end, cancel);
        await assertSlide(page, 0);
      }
      await page.locator('[data-slide="1"]').click();
      await page.locator("#showcase-desktop .showcase-link").focus();
      await page.keyboard.press("ArrowRight");
      await assertSlide(page, 2);
      assert.equal(await showcase.evaluate((element) => element === document.activeElement), true, "focus remains outside hidden slide");
      assert.match(await page.locator(".showcase-status").textContent(), /^3 \/ 3:/);
      await page.locator('[data-slide="0"]').click();
      for (const image of await page.locator(".app-figure .product-image").all()) {
        await image.scrollIntoViewIfNeeded();
        await image.evaluate((img) => img.decode());
        await assertImage(image);
      }
      assert.equal(await page.locator(".product-image").count(), 7);
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
      const layout = await page.evaluate(() => ({ score: window.layoutShiftScore, shifts: window.layoutShifts }));
      assert.ok(layout.score < 0.1, `layout stability at ${width}: ${JSON.stringify(layout)}`);
      layoutScores.push({ width, cls: Number(layout.score.toFixed(4)) });
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
    await page.waitForTimeout(5500);
    await assertSlide(page, 0);
    assert.equal(/setInterval|setTimeout/.test(fs.readFileSync(path.join(root, "script.js"), "utf8")), false, "showcase has no autoplay timer");
    await page.locator(".install-guide summary").click();
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
    for (const width of [1440, 768, 390, 320]) {
      await plain.setViewportSize({ width, height: 844 });
      await plain.goto(base);
      assert.equal(await plain.locator("#site-nav").isVisible(), true);
      assert.equal(await plain.locator(".menu-toggle").isVisible(), false);
      assert.equal(await plain.locator(".button-primary").isVisible(), true);
      assert.equal(await plain.locator(".showcase-controls").isVisible(), false);
      for (const slide of await plain.locator(".showcase-slide").all()) assert.equal(await slide.isVisible(), true);
      for (const image of await plain.locator(".product-image").all()) {
        await image.scrollIntoViewIfNeeded();
        await assertImage(image);
      }
      await plain.locator("summary").first().click();
      assert.equal(await plain.locator("details").first().getAttribute("open"), "");
      assert.equal(await plain.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
    }
    const touchContext = await browser.newContext({ viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true, reducedMotion: "reduce" });
    const touchPage = await touchContext.newPage();
    await touchPage.goto(base);
    await touchPage.locator(".showcase-slides").scrollIntoViewIfNeeded();
    const stageBounds = await touchPage.locator(".showcase-slides").boundingBox();
    const touch = await touchContext.newCDPSession(touchPage);
    const y = Math.max(20, stageBounds.y + 80);
    await touch.send("Input.dispatchTouchEvent", { type: "touchStart", touchPoints: [{ x: 280, y }] });
    await touch.send("Input.dispatchTouchEvent", { type: "touchEnd", touchPoints: [] });
    await assertSlide(touchPage, 0);
    await touch.send("Input.dispatchTouchEvent", { type: "touchStart", touchPoints: [{ x: 280, y }] });
    await touch.send("Input.dispatchTouchEvent", { type: "touchMove", touchPoints: [{ x: 130, y: y + 4 }] });
    await touch.send("Input.dispatchTouchEvent", { type: "touchEnd", touchPoints: [] });
    await assertSlide(touchPage, 1);
    await touch.detach();
    const animatedContext = await browser.newContext({
      viewport: { width: 1440, height: 900 },
      reducedMotion: "no-preference",
    });
    const animated = await animatedContext.newPage();
    await animated.goto(base);
    await animated.locator('[data-slide="1"]').click();
    assert.equal(await animated.locator("#showcase-desktop").evaluate((element) => getComputedStyle(element).animationName), "slide-in");
    await animated.locator(".feature-panel").first().scrollIntoViewIfNeeded();
    await animated.waitForFunction(() =>
      document.querySelector(".feature-panel").classList.contains("is-revealed"),
    );
    assert.equal(
      await animated.locator(".feature-panel").first().isVisible(),
      true,
    );
    assert.equal(
      await animated.evaluate(() =>
        document.fonts.check('700 24px "Be Vietnam Pro"'),
      ),
      true,
    );
    await verifyDemo(browser, base);
    assert.deepEqual(errors, []);
    console.log(`Observed layout shifts: ${JSON.stringify(layoutScores)}`);
    console.log(
      "PASS: 1440/768/390/320 widths, native screenshot ratios/rounded edges, stable gallery bounds, carousel pagination/arrows/keyboard/swipe/focus/no autoplay, assets/base path, logo/favicon, menu, FAQ, clipboard, reveal, reduced motion and no-JS.",
    );
  } finally {
    if (browser) await browser.close();
    await new Promise((resolve) => server.close(resolve));
  }
})().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
