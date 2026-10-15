"use strict";

// Uses an existing Playwright installation; no package download or test framework.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const http = require("node:http");
const { chromium } = require("playwright");

const root = __dirname;
const extensionStoreUrl = 'https://chromewebstore.google.com/detail/yoursafe/lnlgmfdopjjjnjlepnpoohdfgfboheoh';
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
    pathname.replace(/^\/(?:YourSafe|PasswordTool\.Core)\//, "/").replace(/^\//, "") ||
    "home";
  const file = path.resolve(root, path.extname(relative) ? relative : `${relative}.html`);
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

async function offlineContext(browser, options) {
  const context = await browser.newContext(options);
  // Inspect the gtag queue locally without sending test traffic to the real GA4 property.
  await context.route('https://www.googletagmanager.com/**', route => route.fulfill({ contentType: 'text/javascript', body: '' }));
  return context;
}

async function verifyAnalytics(browser, base) {
  const context = await offlineContext(browser, { reducedMotion: 'reduce', viewport: { width: 1440, height: 900 } });
  const page = await context.newPage();
  const queue = () => page.evaluate(() => window.dataLayer.map(command => Array.from(command)));
  for (const route of ['', 'home', 'home.html', 'privacy-policy']) {
    await page.goto(`${base}${route}?private=do-not-track#private-fragment`);
    const tag = page.locator('script[src^="https://www.googletagmanager.com/gtag/js"]');
    assert.equal(await tag.count(), 1, 'one Google tag per document');
    assert.equal(await tag.getAttribute('src'), 'https://www.googletagmanager.com/gtag/js?id=G-1GP0TS974S');
    assert.equal(await tag.getAttribute('async'), '', 'Google tag loads asynchronously');
    assert.equal(await page.locator('script[src="./analytics.js"]').getAttribute('defer'), '', 'local initialization does not block rendering');
    const initial = await queue();
    assert.equal(initial.length, 2, 'only js and config initialize analytics');
    assert.equal(initial[0][0], 'js');
    assert.equal(initial[1][0], 'config');
    assert.equal(initial[1][1], 'G-1GP0TS974S');
    assert.equal(initial[1][2].page_location, `${base}${route}`, 'page URL excludes query and fragment');
    assert.equal(initial[1][2].allow_google_signals, false);
    assert.equal(initial[1][2].allow_ad_personalization_signals, false);
    assert.equal(initial[1][2].send_page_view, undefined, 'config sends the automatic page_view');
    await page.addScriptTag({ url: `${base}analytics.js` });
    assert.deepEqual(await queue(), initial, 'loading initialization twice does not duplicate config or listeners');
    // Cancel navigation after the analytics handler has observed a real user click.
    await page.evaluate(() => {
      document.addEventListener('click', event => event.preventDefault());
      document.addEventListener('auxclick', event => event.preventDefault());
      document.querySelectorAll('details').forEach(details => { details.open = true; });
    });
    if (route !== 'privacy-policy') {
      assert.equal(await page.locator('.faq-privacy a').getAttribute('href'), './privacy-policy');
      await page.locator('[data-slide="2"]').click();
      assert.equal(await page.locator('a[data-analytics-event="download_click"]').count(), 4);
      assert.equal(await page.locator('a[data-analytics-event="github_click"]').count(), 5);
      assert.equal(await page.locator('a[data-analytics-event="extension_click"]').count(), 7);
      assert.equal(await page.locator(`a[href="${extensionStoreUrl}"]`).count(), 3, 'installation links open the published extension');
      for (const selector of ['a[href$="/YourSafe-setup.exe"]', 'a[href="#extension"]', `a[href="${extensionStoreUrl}"]`]) {
        assert.equal(await page.locator(`${selector}:not([data-analytics-event])`).count(), 0, `all relevant ${selector} links are tracked`);
      }
    }
    const links = page.locator('a[data-analytics-event]');
    for (const link of await links.all()) {
      const name = await link.getAttribute('data-analytics-event');
      const location = await link.getAttribute('data-analytics-location');
      if (name === 'download_click') assert.equal(await link.getAttribute('href'), 'https://github.com/Lmnhutw/YourSafe/releases/latest/download/YourSafe-setup.exe', 'download links target the latest installer directly');
      const before = (await queue()).length;
      await link.hover();
      await link.evaluate(element => element.click());
      assert.equal((await queue()).length, before, 'hover and programmatic clicks are not tracked');
      await link.click();
      const after = await queue();
      assert.equal(after.length, before + 1, 'one event per real click');
      const expected = { location };
      if (name === 'download_click') expected.platform = 'windows';
      if (name === 'extension_click') expected.browser = 'chrome_edge_brave';
      assert.deepEqual(after.at(-1), ['event', name, expected], 'only fixed event metadata is collected');
    }
    if (route === 'privacy-policy') {
      for (const width of [390, 320]) {
        await page.setViewportSize({ width, height: 844 });
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true, `analytics disclosure fits at ${width}`);
      }
    }
    const before = (await queue()).length;
    await links.first().press('Enter');
    assert.equal((await queue()).length, before + 1, 'keyboard activation is tracked once');
    await links.first().click({ button: 'middle' });
    assert.equal((await queue()).length, before + 2, 'middle click is tracked once');
    await page.locator('.brand').first().press('Enter');
    assert.equal((await queue()).length, before + 2, 'unmarked links are not tracked');
  }
  await page.route('https://www.googletagmanager.com/**', route => route.abort());
  for (const route of ['privacy', 'privacy.html']) {
    await page.goto(`${base}${route}`);
    await page.waitForURL(`${base}privacy-policy`);
    assert.equal(await page.locator('section[aria-labelledby="privacy-website"] a').first().getAttribute('href'), 'https://policies.google.com/technologies/partner-sites', 'legacy policy links reach the shortened disclosure');
  }
  await page.goto(`${base}home`, { referer: `${base}privacy-policy?email=do-not-track` });
  assert.equal((await queue())[1][2].page_referrer, new URL(base).origin, 'referrer only contains the origin');
  await page.setViewportSize({ width: 390, height: 844 });
  await page.locator('.menu-toggle').click();
  assert.equal(await page.locator('.site-nav').isVisible(), true, 'blocked analytics does not break navigation');
  await page.keyboard.press('Escape');
  await page.evaluate(() => document.addEventListener('click', event => event.preventDefault()));
  await page.locator('.header-download').press('Enter');
  assert.equal((await queue()).at(-1)[1], 'download_click', 'clicks queue safely when Google is blocked');
  await page.goto(`${base}demo-login`);
  assert.equal(await page.locator('.header-download').getAttribute('href'), 'https://github.com/Lmnhutw/YourSafe/releases/latest/download/YourSafe-setup.exe', 'protected demo also links directly to the installer');
  assert.equal(await page.locator('script[src*="analytics"], script[src*="googletagmanager"]').count(), 0, 'credential demo stays free of third-party analytics');
  assert.match(await page.locator('meta[http-equiv="Content-Security-Policy"]').getAttribute('content'), /connect-src 'none'/);
  assert.ok(fs.readFileSync(path.join(root, '../.github/workflows/website-pages.yml'), 'utf8').includes('website/analytics.js'), 'Pages workflow publishes analytics script');
  await context.close();
  console.log('PASS: GA4 async tag, automatic page_view config, duplicate initialization guard, all download/repository/extension links, actual clicks only, fixed parameters, clean page URLs and protected demo.');
}

async function assertSlide(page, index) {
  const state = await page.locator(".showcase").evaluate((showcase) => ({
    slides: [...showcase.querySelectorAll(".showcase-slide")].map((slide) => ({ hidden: slide.hidden, inert: slide.inert, ariaHidden: slide.getAttribute('aria-hidden') })),
    current: [...showcase.querySelectorAll("[data-slide]")].map((button) => button.getAttribute("aria-current")),
  }));
  state.slides.forEach((slide, position) => {
    assert.equal(slide.inert, position !== index);
    assert.equal(slide.ariaHidden, String(position !== index));
    if (position === index) assert.equal(slide.hidden, false);
  });
  assert.deepEqual(state.current, [0, 1, 2].map((position) => String(position === index)));
}

async function verifySliderFade(browser, base) {
  const context = await offlineContext(browser, { reducedMotion: 'no-preference' });
  const page = await context.newPage();
  for (const width of [1440, 390]) {
    await page.setViewportSize({ width, height: 900 });
    await page.goto(base);
    assert.equal(await page.locator('.showcase-autoplay').count(), 0);
    await page.locator('[data-direction="1"]').click();
    const motion = await page.locator('.showcase-slides').evaluate(stage => {
      const animations = stage.getAnimations({ subtree: true });
      animations.forEach(animation => { animation.pause(); animation.currentTime = 200; });
      const bounds = stage.getBoundingClientRect();
      return { frames: animations.map(animation => animation.effect.getKeyframes().map(frame => frame.opacity)),
        offsets: [...stage.querySelectorAll('.showcase-slide:not([hidden])')].map(slide => slide.getBoundingClientRect().x - bounds.x),
        opacity: [...stage.querySelectorAll('.showcase-slide:not([hidden])')].map(slide => Number(getComputedStyle(slide).opacity)) };
    });
    assert.deepEqual(motion.frames, [['1', '0'], ['0', '1']]);
    assert.ok(motion.offsets.every(x => Math.abs(x) < 1), 'fade does not move the slides horizontally');
    assert.ok(motion.opacity.every(value => value > 0 && value < 1), 'both slides crossfade');
    await page.locator('.showcase-slides').evaluate(stage => stage.getAnimations({ subtree: true }).forEach(animation => animation.play()));
    await page.waitForFunction(() => document.querySelector('.showcase-slides').getAnimations({ subtree: true }).length === 0);
    assert.equal(await page.locator('.showcase-slide:not([hidden])').count(), 1, 'outgoing slide is removed after motion');
    await page.locator('[data-direction="-1"]').click();
    const reverse = await page.locator('.showcase-slides').evaluate(stage => stage.getAnimations({ subtree: true }).map(animation => animation.effect.getKeyframes().map(frame => frame.opacity)));
    assert.deepEqual(reverse, [['1', '0'], ['0', '1']]);
    await page.locator('[data-direction="1"]').click();
    await page.locator('[data-direction="1"]').click();
    await assertSlide(page, 2);
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await page.waitForFunction(() => document.querySelector('.showcase-slides').getAnimations({ subtree: true }).length === 0);
    assert.equal(await page.locator('.showcase-slide:not([hidden])').count(), 1, 'reduced motion cancels the transition');
    await page.emulateMedia({ reducedMotion: 'no-preference' });
  }
  await context.close();
}

async function verifyPrivacyAndClosing(browser, base) {
  const context = await offlineContext(browser, { reducedMotion: 'reduce' });
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  for (const width of [1920, 1440, 1280, 1100, 1024, 961, 960, 768, 681, 680, 390, 320]) {
    await page.setViewportSize({ width, height: 900 });
    await page.goto(base);
    await page.evaluate(() => document.fonts.ready);
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true, `home fits at ${width}`);
    assert.equal(await page.locator('.site-nav a[href="./demo-login"]').textContent(), 'Demo');
    assert.equal(await page.locator('.site-nav a[href="#extension"]').textContent(), 'Tiện ích');
    assert.equal(await page.locator('.extension-actions .button').getAttribute('href'), extensionStoreUrl);
    assert.equal(await page.locator('.extension-actions .text-link').getAttribute('href'), './demo-login');
    assert.equal(await page.locator('.closing-extension-actions a[href="#extension"]').count(), 1);
    const extensionLayout = await page.locator('.feature-extension').evaluate(panel => {
      const title = panel.querySelector('h3').getBoundingClientRect();
      const bounds = panel.getBoundingClientRect();
      const images = [...panel.querySelectorAll('.extension-visuals .product-image')].map(image => image.getBoundingClientRect());
      const download = panel.querySelector('.extension-actions .button').getBoundingClientRect();
      const demo = panel.querySelector('.extension-actions .text-link').getBoundingClientRect();
      return {
        imagesFit: images.length === 2 && images.every(image => image.top >= title.top && image.bottom <= bounds.bottom && image.left >= bounds.left && image.right <= bounds.right),
        actionsBeside: download.right < demo.left && Math.abs((download.top + download.bottom) / 2 - (demo.top + demo.bottom) / 2) < 1,
      };
    });
    assert.equal(extensionLayout.imagesFit, true, `extension images stay below title and inside panel at ${width}`);
    if (width >= 1280) assert.equal(extensionLayout.actionsBeside, true, `download and demo actions sit beside each other at ${width}`);
    if (width <= 960) {
      await page.getByRole('button', { name: 'Menu' }).click();
      assert.equal(await page.getByRole('link', { name: 'Demo', exact: true }).isVisible(), true);
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true, `open menu fits at ${width}`);
      await page.keyboard.press('Escape');
    }
    const socialsAbove = await page.locator('.footer-copyright').evaluate(element => {
      return element.querySelector('.footer-socials').getBoundingClientRect().bottom <= element.querySelector('p').getBoundingClientRect().top;
    });
    assert.equal(socialsAbove, true, `footer icons above copyright at ${width}`);
    const link = page.locator('.faq-privacy a');
    assert.equal(await link.getAttribute('href'), './privacy-policy');
    if (width > 960) {
      const installHeadings = await page.locator('.install-steps h3').evaluateAll(headings => headings.map(heading => heading.getBoundingClientRect().top));
      assert.ok(Math.max(...installHeadings) - Math.min(...installHeadings) < 1, `installation steps align at ${width}`);
      const layout = await page.evaluate(() => ({
        footer: document.querySelector('.site-footer').getBoundingClientRect().toJSON(),
        closing: document.querySelector('.closing-inner').getBoundingClientRect().toJSON(),
        section: document.querySelector('.closing').getBoundingClientRect().toJSON(),
        brand: document.querySelector('.footer-brand').getBoundingClientRect().toJSON(),
        feedback: document.querySelector('.footer-feedback').getBoundingClientRect().toJSON(),
        feedbackText: document.querySelector('.footer-feedback p').getBoundingClientRect().toJSON(),
      }));
      assert.ok(layout.footer.height < 160, `compact footer at ${width}`);
      assert.ok(layout.section.height >= 464, `restored CTA height at ${width}`);
      assert.ok(Math.abs(layout.closing.x - Math.min(width * 0.0525, 100)) < 1, `CTA desktop gutter at ${width}`);
      assert.ok(Math.abs(layout.closing.x - (width - layout.closing.right)) < 1, `equal CTA outer spacing at ${width}`);
      assert.ok(Math.abs(layout.brand.x - layout.closing.x) < 1 && Math.abs(layout.feedback.right - layout.closing.right) < 1, `footer aligns with CTA at ${width}`);
      assert.ok(Math.abs(layout.feedbackText.right - layout.feedback.right) < 1, `feedback text uses its full column at ${width}`);
      assert.equal(layout.footer.width, width, `footer spans desktop at ${width}`);
    }
    if ([1920, 1440, 390].includes(width)) {
      await page.locator('.feature-extension').screenshot({ path: path.join(root, `../.impeccable/review/extension-install-${width}.png`), style: '.site-header, .skip-link { visibility: hidden !important; }' });
      await page.locator('#faq').screenshot({ path: path.join(root, `../.impeccable/review/faq-${width}.png`) });
      await page.locator('.closing').screenshot({ path: path.join(root, `../.impeccable/review/closing-${width}.png`) });
      await page.locator('.site-footer').screenshot({ path: path.join(root, `../.impeccable/review/footer-${width}.png`) });
    }
    if ([1440, 390].includes(width)) {
      await page.locator('.install-guide summary').click();
      await page.locator('#install').screenshot({ path: path.join(root, `../.impeccable/review/install-simple-${width}.png`), style: '.site-header, .skip-link { visibility: hidden !important; }' });
      await page.locator('.install-guide summary').click();
    }
    await link.click();
    assert.equal(page.url(), `${base}privacy-policy`);
    assert.equal(await page.title(), 'Chính sách quyền riêng tư — YourSafe');
    assert.equal(await page.locator('.privacy h1').textContent(), 'Chính sách quyền riêng tư');
    assert.equal(await page.locator('.privacy li').count(), 4);
    assert.equal(await page.locator('.privacy a[href^="mailto:"]').getAttribute('href'), 'mailto:lmnhut0211@gmail.com');
    await page.evaluate(() => document.fonts.ready);
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true, `privacy fits at ${width}`);
    if ([1440, 390].includes(width)) await page.screenshot({ path: path.join(root, `../.impeccable/review/privacy-${width}.png`), fullPage: true });
    await page.getByRole('link', { name: 'Về hỏi đáp' }).click();
    assert.equal(page.url(), `${base}home#faq`);
  }
  assert.deepEqual(errors, []);
  await context.close();
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

async function verifyStorageLayout(browser, base) {
  const context = await offlineContext(browser, { reducedMotion: 'reduce' });
  const page = await context.newPage();
  for (const width of [1920, 1536, 1440, 1280, 1024, 961, 960, 768, 681, 680, 390, 320]) {
    await page.setViewportSize({ width, height: 900 });
    await page.goto(base);
    await page.evaluate(() => document.fonts.ready);
    const layout = await page.locator('.storage-figure').evaluate(figure => {
      const bounds = figure.getBoundingClientRect();
      const items = [...figure.querySelectorAll('.storage-origin-title, .storage-computer, .storage-transfer, .storage-backup-title, .storage-destination')];
      const boxes = items.map(item => item.getBoundingClientRect());
      return {
        fits: items.every((item, index) => boxes[index].left >= bounds.left - 1 && boxes[index].right <= bounds.right + 1 && item.scrollWidth <= item.clientWidth + 1),
        inline: figure.querySelector('.storage-origin-title[style], .storage-backup-title[style]') !== null,
        labels: figure.getAttribute('aria-labelledby').split(' ').every(id => document.querySelectorAll(`#${id}`).length === 1),
        stacked: boxes.slice(1).every((box, index) => box.top >= boxes[index].bottom - 1),
        aligned: Math.abs(boxes[0].top - boxes[3].top) < 1,
      };
    });
    assert.equal(layout.fits, true, `storage titles and diagram fit at ${width}`);
    assert.equal(layout.inline, false, 'static storage styles belong in CSS');
    assert.equal(layout.labels, true, 'storage labels have unique IDs');
    assert.equal(width <= 680 ? layout.stacked : layout.aligned, true, `storage heading order at ${width}`);
    if ([1440, 768, 390].includes(width)) {
      await page.locator('.storage-figure').screenshot({ path: path.join(root, `../.impeccable/review/storage-${width}.png`) });
    }
  }
  await context.close();
  console.log('PASS: storage headings, unique accessible labels, no inline CSS and responsive layout at 12 widths from 320 to 1920.');
}

async function verifyStorage(browser, base) {
  const context = await offlineContext(browser, { reducedMotion: 'no-preference', viewport: { width: 1440, height: 900 } });
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.goto(base);
  assert.equal(await page.locator('.storage-figure').evaluate(element => element.classList.contains('is-playing')), false, 'storage waits until visible');
  await page.locator('.storage-packet').evaluate(packet => {
    window.storageIterations = 0;
    window.storageEnds = 0;
    packet.addEventListener('animationiteration', () => window.storageIterations++);
    packet.addEventListener('animationend', () => window.storageEnds++);
  });
  await page.locator('.storage-figure').scrollIntoViewIfNeeded();
  await page.waitForFunction(() => document.querySelector('.storage-figure').classList.contains('is-playing'));
  assert.equal(await page.locator('.storage-replay').isDisabled(), true, 'no overlapping replay');
  const origin = await page.locator('.storage-screen').boundingBox();
  await page.waitForFunction(() => !document.querySelector('.storage-figure').classList.contains('is-playing'));
  assert.deepEqual(await page.evaluate(() => [window.storageIterations, window.storageEnds]), [2, 1], 'exactly three cycles then stop');
  assert.deepEqual(await page.locator('.storage-screen').boundingBox(), origin, 'original vault stays in place');
  assert.equal(await page.locator('.storage-replay').isEnabled(), true);
  await page.locator('.storage-replay').focus();
  await page.keyboard.press('Enter');
  await page.waitForFunction(() => document.querySelector('.storage-figure').classList.contains('is-playing'));
  await page.waitForFunction(() => !document.querySelector('.storage-figure').classList.contains('is-playing'));
  assert.deepEqual(await page.evaluate(() => [window.storageIterations, window.storageEnds]), [4, 2], 'keyboard text action replays three cycles');
  await page.evaluate(() => scrollTo({ top: 0, behavior: 'instant' }));
  await page.locator('.storage-figure').scrollIntoViewIfNeeded();
  assert.equal(await page.locator('.storage-figure').evaluate(element => element.classList.contains('is-playing')), false, 'scrolling back does not restart autoplay');
  await page.locator('.storage-replay').click();
  await page.waitForFunction(() => document.querySelector('.storage-figure').classList.contains('is-playing'));
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await page.waitForFunction(() => !document.querySelector('.storage-figure').classList.contains('is-playing'));
  assert.equal(await page.locator('.storage-replay').isDisabled(), true, 'reduced motion cancels and disables replay');
  await page.setViewportSize({ width: 390, height: 900 });
  await page.emulateMedia({ reducedMotion: 'no-preference' });
  await page.waitForFunction(() => !document.querySelector('.storage-replay').disabled);
  await page.locator('.storage-replay').click();
  const path = await page.locator('.storage-packet').evaluate(packet => ({ x: packet.style.getPropertyValue('--copy-x'), y: parseFloat(packet.style.getPropertyValue('--copy-y')) }));
  assert.equal(path.x, '0px', 'mobile copy moves vertically');
  assert.ok(path.y > 0);
  await page.waitForFunction(() => !document.querySelector('.storage-figure').classList.contains('is-playing'));
  assert.deepEqual(errors, []);
  await context.close();
  console.log('PASS: storage starts on visibility, three finite cycles, original retained, keyboard/click text replay, no autoplay on re-entry, reduced-motion cancellation and vertical mobile path.');
}

async function verifyDemo(browser, base) {
  const { buildSync } = require('../browser-extension/node_modules/esbuild');
  const extensionFields = buildSync({ entryPoints: [path.join(root, '../browser-extension/src/fields.ts')],
    bundle: true, write: false, format: 'iife', globalName: 'YourSafeFields' }).outputFiles[0].text;
  const context = await offlineContext(browser, { reducedMotion: 'reduce' });
  await context.route(`${base}extension-fields.js`, route => route.fulfill({ contentType: 'text/javascript', body: extensionFields }));
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('response', response => { if (response.status() >= 400) errors.push(`${response.status()} ${response.url()}`); });
  await page.goto(base);
  await page.locator('.closing-demo a[href="./demo-login"]').click();
  assert.equal(page.url(), `${base}demo-login`);
  for (const width of [1440, 768, 390, 320]) {
    await page.setViewportSize({ width, height: 900 });
    await page.goto(`${base}demo-login`);
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
  await page.goto(`${base}demo-login`);
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
  assert.equal(page.url(), `${base}demo-login`);
  assert.deepEqual(await page.evaluate(() => ({ local: localStorage.length, session: sessionStorage.length, cookies: document.cookie })), { local: 0, session: 0, cookies: '' });
  await page.evaluate(() => Object.defineProperty(navigator.clipboard, 'writeText', { configurable: true, value: async text => { window.copiedAddress = text; } }));
  await page.getByRole('button', { name: 'Sao chép', exact: true }).click();
  assert.equal(await page.evaluate(() => window.copiedAddress), `${base}demo-login`);
  await page.evaluate(() => Object.defineProperty(navigator.clipboard, 'writeText', { value: async () => { throw new Error('denied'); } }));
  await page.getByRole('button', { name: 'Sao chép', exact: true }).click();
  assert.match(await page.locator('#address-status').textContent(), /Không sao chép được/);
  await page.getByLabel('Tên đăng nhập').fill('temporary-demo');
  await page.getByRole('link', { name: 'Về trang chủ' }).click();
  assert.equal(page.url(), `${base}home`);
  await page.goBack();
  assert.equal(await page.getByLabel('Tên đăng nhập').inputValue(), '', 'leaving the page clears inputs');
  await page.goto(new URL('/demo-login', base).href);
  assert.equal(await page.locator('#demo-address').textContent(), new URL('/demo-login', base).href);
  await page.goto(`${base}demo-login.html`);
  assert.equal(await page.locator('#demo-address').textContent(), `${base}demo-login`, 'legacy HTML entry displays the clean demo URL');
  assert.deepEqual(errors, []);
  await context.close();
  const noJs = await offlineContext(browser, { javaScriptEnabled: false });
  const plain = await noJs.newPage();
  await plain.goto(`${base}demo-login`);
  assert.equal(await plain.getByRole('button', { name: 'Đăng nhập', exact: true }).isDisabled(), true);
  assert.equal(await plain.getByLabel('Tên đăng nhập').isDisabled(), true, 'no-JS cannot submit secrets');
  await noJs.close();
  const blockedJs = await offlineContext(browser);
  await blockedJs.route('**/demo-login.js', route => route.abort());
  const blocked = await blockedJs.newPage();
  await blocked.goto(`${base}demo-login`);
  assert.equal(await blocked.getByLabel('Tên đăng nhập').isDisabled(), true, 'missing script cannot submit secrets');
  await blockedJs.close();
  console.log('PASS: demo CTA/base paths, 1440/768/390/320 layouts, actual extension field detection/fill, keyboard submit, arbitrary/empty input, reset/focus, no secret requests/storage, copy success/failure and no-JS/script-failure safety.');
}

(async () => {
  let browser;
  try {
    await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
    const base = `http://127.0.0.1:${server.address().port}/YourSafe/`;
    browser = await chromium.launch({
      headless: true,
      channel: process.platform === "win32" ? "msedge" : undefined,
    });
    await verifyAnalytics(browser, base);
    if (process.argv.includes('--analytics-only')) return;
    const errors = [];
    const layoutScores = [];
    const context = await offlineContext(browser, { reducedMotion: "reduce" });
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
    await verifyStorageLayout(browser, base);
    for (const width of [1440, 768, 390, 320]) {
      await page.setViewportSize({ width, height: 900 });
      await page.goto(base);
      await page.evaluate(() => document.fonts.ready);
      if ([1440, 390].includes(width)) await page.locator('.site-header').screenshot({ path: path.join(root, `../.impeccable/review/header-${width}.png`) });
      const compactHeader = await page.locator('.site-header').boundingBox();
      const compactDownload = await page.locator('.header-download').boundingBox();
      assert.ok(compactHeader.height <= 72, `compact header at ${width}`);
      assert.ok(compactDownload.height >= 44 && compactDownload.height <= 45, `compact accessible download at ${width}`);
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
      assert.equal(await page.locator('.manual-bottom svg').count(), 0, 'large footer arrow removed');
      assert.equal(await page.locator('.manual-demo').count(), 1, 'demo caption moved once');
      assert.equal(await page.locator('.manual-demo a').getAttribute('href'), './demo-login');
      assert.equal(await page.locator('.extension-context figcaption').count(), 1, 'preview caption belongs to its full-width figure');
      const footerFits = await page.locator('.manual-bottom').evaluate((footer) => {
        const article = footer.closest('.showcase-slide').getBoundingClientRect();
        const caption = footer.querySelector('.manual-demo').getBoundingClientRect();
        const rows = footer.previousElementSibling.getBoundingClientRect();
        return caption.bottom <= article.bottom && caption.left >= article.left && caption.right <= article.right && rows.bottom <= caption.top;
      });
      assert.equal(footerFits, true, `first slide caption fits at ${width}`);
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
      assert.equal(await page.locator('a[href^="./assets/"]').count(), 0, 'product images have no click-through links');
      for (const image of await page.locator('.app-figure .product-image:not([data-no-preview])').all()) {
        await image.hover();
        assert.equal(await page.locator('.image-preview').isVisible(), true, 'shared hover preview');
        const fits = await page.locator('.image-preview > *').evaluate(element => {
          const bounds = element.getBoundingClientRect();
          return bounds.left >= 0 && bounds.top >= 0 && bounds.right <= innerWidth && bounds.bottom <= innerHeight;
        });
        assert.equal(fits, true, `preview fits viewport at ${width}`);
        await image.click();
        assert.equal(page.url(), base, 'clicking an image does not navigate');
        await page.mouse.move(0, 0);
        assert.equal(await page.locator('.image-preview').isVisible(), false, 'leaving closes preview');
      }
      await page.keyboard.press('Tab');
      await page.keyboard.press('Shift+Tab');
      assert.equal(await page.locator('.image-preview').isVisible(), true, 'keyboard image preview');
      await page.keyboard.press('Escape');
      assert.equal(await page.locator('.image-preview').isVisible(), false);
      await page.evaluate(() => document.activeElement.blur());
      for (const image of await page.locator('.product-image[data-no-preview]').all()) {
        await image.hover();
        assert.equal(await image.getAttribute('tabindex'), null);
        assert.equal(await page.locator('.image-preview').isVisible(), false, 'large images do not open a preview');
      }
      assert.equal(await page.locator('.product-image[data-no-preview]').count(), 2);
      assert.equal(await page.locator('.hero-foot a').count(), 0);
      assert.equal(await page.locator(`.download-actions .download-extension[href="${extensionStoreUrl}"]`).count(), 2);
      for (const caption of await page.locator('figcaption, .visual-note, .showcase-footer p, .manual-demo').all()) {
        const style = await caption.evaluate(element => {
          const css = getComputedStyle(element);
          return { size: css.fontSize, opacity: css.opacity, align: css.textAlign };
        });
        assert.deepEqual(style, { size: '12.8px', opacity: '0.85', align: 'center' });
      }
      assert.equal(await page.locator('.storage-figure .button').count(), 0, 'no large illustration button');
      assert.equal(await page.locator('.storage-replay span').innerText(), 'Bản sao mã hóa');
      assert.equal(await page.locator('.storage-replay').isDisabled(), true, 'reduced motion shows static diagram');
      const storageFits = await page.locator('.storage-figure').evaluate(figure => {
        const bounds = figure.getBoundingClientRect();
        return [...figure.querySelectorAll('.storage-screen, .storage-device-title, .storage-vault, .storage-destination, .storage-replay')].every(element => {
          const item = element.getBoundingClientRect();
          return item.left >= bounds.left && item.right <= bounds.right + 1;
        });
      });
      assert.equal(storageFits, true, `storage scene fits at ${width}`);
      if ([1440, 390].includes(width)) await page.locator('.storage-figure').screenshot({ path: path.join(root, `../.impeccable/review/storage-${width}.png`) });
      assert.equal(await page.locator(".product-image").count(), 7);
      await page.evaluate(() => scrollTo(0, 0));
      for (const href of await page
        .locator('a[href^="#"]:not([href="#"])')
        .evaluateAll((links) =>
          links.map((link) => link.getAttribute("href")),
        )) {
        assert.equal(await page.locator(href).count(), 1, `anchor ${href}`);
      }
      assert.equal(
        await page
          .locator(
            'a[href="https://github.com/Lmnhutw/YourSafe/releases/latest/download/YourSafe-setup.exe"]',
          )
          .count(),
        4,
      );
      assert.equal(
        await page.locator('.brand-logo[src="./assets/app-logo.png"]').count(),
        2,
      );
      assert.equal(
        await page.locator('link[rel="icon"]').getAttribute("href"),
        "./assets/app-logo.png",
      );
      assert.equal(await page.locator('.site-header > :last-child').getAttribute('class'), 'button button-primary header-download');
      assert.equal(await page.locator('.site-nav a[href="./demo-login"]').textContent(), 'Demo');
      if (width <= 960) {
        const closedHeader = await page.locator('.site-header').boundingBox();
        await page.getByRole("button", { name: "Menu" }).click();
        const openHeader = await page.locator('.site-header').boundingBox();
        const menu = await page.locator('.site-nav').boundingBox();
        assert.equal(openHeader.height, closedHeader.height, 'opening menu keeps header compact');
        assert.ok(menu.y >= openHeader.y + openHeader.height, 'menu opens below header');
        assert.ok(menu.x >= 0 && menu.x + menu.width <= width, 'menu fits viewport');
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
      if (width <= 960) await page.locator('.menu-toggle').click();
      for (const link of await page.locator('.site-nav a').all()) {
        await page.mouse.move(0, 0);
        assert.equal(await link.evaluate(element => getComputedStyle(element).textDecorationLine), 'none', 'no permanent navigation underline');
        await link.hover();
        assert.equal(await link.evaluate(element => getComputedStyle(element).textDecorationLine), 'underline', 'navigation underlines on hover');
      }
      await page.locator('.site-nav a[href="#features"]').click();
      const header = await page.locator('.site-header').boundingBox();
      const heading = await page.locator('#features').boundingBox();
      assert.equal(header.y, 0, 'header sticks while scrolling');
      assert.ok(heading.y >= header.height, `anchor clears sticky header at ${width}`);
      assert.equal(await page.locator('.footer-brand p').textContent(), 'Lưu mật khẩu trên máy Windows của bạn.');
      assert.equal(await page.locator('.footer-copyright strong').textContent(), '© 2026 YourSafe.');
      assert.equal(await page.getByRole('link', { name: 'LinkedIn của Minh Nhựt' }).getAttribute('href'), 'https://www.linkedin.com/in/minhnhut0211/');
      assert.equal(await page.locator('.footer-socials a').count(), 2);
      assert.equal(await page.locator('.footer-feedback form, .footer-feedback textarea, .footer-feedback input, .footer-feedback button').count(), 0, 'feedback is plain text with links');
      assert.equal(await page.locator('.footer-feedback a[href^="mailto:"]').getAttribute('href'), 'mailto:lmnhut0211@gmail.com');
      assert.equal(await page.locator('.footer-feedback a[href^="https:"]').getAttribute('href'), 'https://github.com/Lmnhutw/PasswordTool.Core/issues/new');
      const footerLayout = await page.locator('.site-footer').evaluate(footer => {
        const brand = footer.querySelector('.footer-brand').getBoundingClientRect();
        const copyright = footer.querySelector('.footer-copyright').getBoundingClientRect();
        const feedback = footer.querySelector('.footer-feedback').getBoundingClientRect();
        if (innerWidth <= 680) return feedback.top > brand.bottom && copyright.top > feedback.bottom;
        return innerWidth > 960 ? brand.right < copyright.left && copyright.right < feedback.left : feedback.top > brand.bottom && feedback.top > copyright.bottom;
      });
      assert.equal(footerLayout, true, `footer layout at ${width}`);
      if ([1440, 390].includes(width)) await page.locator('.site-footer').screenshot({ path: path.join(root, `../.impeccable/review/footer-${width}.png`) });
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
    assert.equal(await page.locator('.showcase-autoplay').count(), 0, 'no stop button');
    assert.equal(await page.locator('.showcase-arrows button svg').count(), 2);
    for (const arrow of await page.locator('.showcase-arrows button').all()) {
      assert.equal(await arrow.evaluate(element => getComputedStyle(element).borderTopWidth), '0px', 'arrows have no box outline');
      await arrow.hover();
      assert.equal(await arrow.evaluate(element => getComputedStyle(element).backgroundColor), 'rgba(0, 0, 0, 0)', 'arrow hover keeps the background clear');
    }
    await page.locator(".install-guide summary").click();
    assert.match(await page.locator('.install-guide').innerText(), /Giải nén tất cả/);
    assert.doesNotMatch(await page.locator('#install').innerText(), /SHA-256|PowerShell|SIGNED|checksums/);
    const noJs = await offlineContext(browser, {
      javaScriptEnabled: false,
      viewport: { width: 390, height: 844 },
    });
    const plain = await noJs.newPage();
    for (const width of [1440, 768, 390, 320]) {
      await plain.setViewportSize({ width, height: 844 });
      await plain.goto(base);
      assert.equal(await plain.locator("#site-nav").isVisible(), true);
      assert.equal(await plain.locator(".menu-toggle").isVisible(), false);
      assert.equal(await plain.locator(".hero-copy .button-primary").isVisible(), true);
      assert.equal(await plain.locator('.footer-feedback a[href^="https:"]').getAttribute('href'), 'https://github.com/Lmnhutw/PasswordTool.Core/issues/new');
      assert.equal(await plain.locator(".showcase-controls").isVisible(), false);
      assert.equal(await plain.locator('.storage-contents li').count(), 3, 'static storage labels without JavaScript');
      assert.equal(await plain.locator('.storage-replay').isDisabled(), true);
      for (const slide of await plain.locator(".showcase-slide").all()) assert.equal(await slide.isVisible(), true);
      for (const image of await plain.locator(".product-image").all()) {
        await image.scrollIntoViewIfNeeded();
        await assertImage(image);
      }
      await plain.locator("summary").first().click();
      assert.equal(await plain.locator("details").first().getAttribute("open"), "");
      assert.equal(await plain.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
    }
    const touchContext = await offlineContext(browser, { viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true, reducedMotion: "reduce" });
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
    await touchPage.locator('#showcase-desktop .product-image').tap();
    assert.equal(await touchPage.locator('.image-preview').isVisible(), false, 'touch does not open a hover preview');
    assert.equal(touchPage.url(), base);
    await touch.detach();
    const animatedContext = await offlineContext(browser, {
      viewport: { width: 1440, height: 900 },
      reducedMotion: "no-preference",
    });
    const animated = await animatedContext.newPage();
    await animated.clock.install();
    await animated.clock.pauseAt(new Date());
    await animated.goto(base);
    for (const index of [1, 2, 0]) {
      await animated.clock.runFor(5000);
      await assertSlide(animated, index);
    }
    assert.equal(await animated.locator('.showcase-status').textContent(), '', 'autoplay does not announce every slide');
    await animated.locator('.showcase').hover();
    await animated.clock.runFor(10000);
    await assertSlide(animated, 0);
    await animated.mouse.move(0, 0);
    await animated.clock.runFor(5000);
    await assertSlide(animated, 1);
    await animated.locator('#showcase-desktop .showcase-link').focus();
    await animated.clock.runFor(10000);
    await assertSlide(animated, 1);
    await animated.evaluate(() => document.activeElement.blur());
    await animated.clock.runFor(5000);
    await assertSlide(animated, 2);
    await animated.locator('[data-slide="2"]').focus();
    await animated.clock.runFor(10000);
    await assertSlide(animated, 2);
    await animated.mouse.move(0, 0);
    await animated.evaluate(() => document.activeElement.blur());
    await animated.clock.runFor(5000);
    await assertSlide(animated, 0);
    await animated.evaluate(() => {
      Object.defineProperty(document, 'hidden', { configurable: true, value: true });
      document.dispatchEvent(new Event('visibilitychange'));
    });
    await animated.clock.runFor(10000);
    await assertSlide(animated, 0);
    await animated.evaluate(() => {
      delete document.hidden;
      document.dispatchEvent(new Event('visibilitychange'));
    });
    await animated.clock.runFor(5000);
    await assertSlide(animated, 1);
    await animated.emulateMedia({ reducedMotion: 'reduce' });
    // Media-query change events arrive on a rendering frame before virtual time advances.
    await new Promise(resolve => setTimeout(resolve, 50));
    await animated.clock.runFor(50);
    assert.equal(await animated.locator('.showcase-slides').evaluate(element => element.getAnimations({ subtree: true }).length), 0, 'reduced motion cancels fades');
    await animated.clock.runFor(10000);
    await assertSlide(animated, 1);
    await animated.emulateMedia({ reducedMotion: 'no-preference' });
    await new Promise(resolve => setTimeout(resolve, 50));
    await animated.clock.runFor(50);
    await animated.locator('[data-slide="2"]').click();
    assert.equal(await animated.locator('.showcase-slides').evaluate(element => element.getAnimations({ subtree: true }).length), 2);
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
    await verifyStorage(browser, base);
    await verifySliderFade(browser, base);
    await verifyPrivacyAndClosing(browser, base);
    await verifyDemo(browser, base);
    assert.deepEqual(errors, []);
    console.log(`Observed layout shifts: ${JSON.stringify(layoutScores)}`);
    console.log(
      "PASS: 1440/768/390/320 widths, sticky header/anchors/download/navigation hover, footer layout/socials/feedback links, image hover/keyboard/touch/no click-through, moved demo caption, native screenshot ratios/rounded edges, stable gallery bounds, carousel pagination/arrows/keyboard/swipe/focus/autoplay/stop/resume/hover/hidden/reduced-motion, assets/base path, logo/favicon, menu, FAQ, clipboard, reveal and no-JS.",
    );
  } finally {
    if (browser) await browser.close();
    await new Promise((resolve) => server.close(resolve));
  }
})().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
