// Capture the actual extension UI with synthetic transport responses, never a real vault.
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require('playwright');
const { buildSync } = require('../browser-extension/node_modules/esbuild');
const root = path.resolve(__dirname, '..');
const source = path.join(root, 'browser-extension/static');
const output = path.join(root, 'website/assets/homepage');
const script = buildSync({ entryPoints: [path.join(root, 'browser-extension/src/popup.ts')], bundle: true, write: false, format: 'iife' }).outputFiles[0].text;
const css = fs.readFileSync(path.join(source, 'popup.css'), 'utf8');
const logo = fs.readFileSync(path.join(root, 'website/assets/app-logo.png')).toString('base64');
const html = fs.readFileSync(path.join(source, 'popup.html'), 'utf8').replace('<link rel="stylesheet" href="popup.css">', `<style>${css}</style>`).replace('icons/app-logo.png', `data:image/png;base64,${logo}`).replace('<script src="popup.js"></script>', '');

async function captureLoginContext(browser) {
  const site = 'https://lmnhutw.github.io/PasswordTool.Core/';
  const page = await browser.newPage({ viewport: { width: 1600, height: 900 }, colorScheme: 'light', reducedMotion: 'reduce' });
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.addInitScript(() => {
    if (!location.pathname.endsWith('/popup-preview.html')) return;
    const listeners = [];
    window.chrome = { runtime: { connect: () => ({
      onMessage: { addListener: fn => listeners.push(fn) },
      onDisconnect: { addListener: () => {} }, disconnect: () => {},
      postMessage: envelope => {
        const action = envelope.message.action;
        const result = action === 'resume' ? { resumed: false }
          : action === 'discover' ? { token: 'demo-only', origin: 'https://lmnhutw.github.io', truncated: false, accounts: [
            { id: '11111111-1111-1111-1111-111111111111', title: 'YourSafe · Demo', username: 'alex.demo@example.test', hasPassword: true, hasTotp: false }
          ] } : { filled: true };
        queueMicrotask(() => listeners.forEach(fn => fn({ requestId: envelope.requestId, ok: true, result })));
      }
    }) } };
  });
  const wrapper = `<!doctype html><html lang="vi"><meta charset="utf-8"><title>Minh họa YourSafe</title>
    <style>body{margin:0;background:#e9ede5;font:14px/1.5 "Segoe UI",sans-serif;color:#182321}
    header{height:48px;display:flex;align-items:center;justify-content:space-between;padding:0 24px;background:#fff}
    main{display:grid;grid-template-columns:minmax(0,1fr) 360px;gap:24px;padding:24px;height:852px;box-sizing:border-box}
    iframe{width:100%;height:100%;border:0;border-radius:12px;background:#f5f6f2}
    aside iframe{height:520px;background:#f7f9fc}</style>
    <header><span>lmnhutw.github.io/PasswordTool.Core/demo-login.html</span><span>Minh họa · dữ liệu demo</span></header>
    <main><iframe title="Trang đăng nhập thử" src="${site}demo-login.html"></iframe><aside><iframe title="Popup YourSafe" src="${site}popup-preview.html"></iframe></aside></main></html>`;
  const files = new Map([
    ['demo-login.html', 'text/html'], ['demo-login.js', 'text/javascript'], ['styles.css', 'text/css'],
    ['assets/BeVietnamPro-Regular.ttf', 'font/ttf'], ['assets/BeVietnamPro-Bold.ttf', 'font/ttf'], ['assets/app-logo.png', 'image/png']
  ]);
  await page.route(`${site}**`, route => {
    const relative = new URL(route.request().url()).pathname.slice('/PasswordTool.Core/'.length);
    if (relative === 'context-preview.html') return route.fulfill({ contentType: 'text/html', body: wrapper });
    if (relative === 'popup-preview.html') return route.fulfill({ contentType: 'text/html', body: html + '<script src="./popup-preview.js"></script>' });
    if (relative === 'popup-preview.js') return route.fulfill({ contentType: 'text/javascript', body: script });
    if (!files.has(relative)) return route.abort();
    return route.fulfill({ contentType: files.get(relative), body: fs.readFileSync(path.join(root, 'website', relative)) });
  });
  await page.goto(`${site}context-preview.html`);
  const demo = page.frameLocator('iframe[title="Trang đăng nhập thử"]');
  const popup = page.frameLocator('iframe[title="Popup YourSafe"]');
  await popup.locator('.account').waitFor();
  // Illustrative values only: this capture does not connect to Native Messaging.
  await demo.getByLabel('Tên đăng nhập').fill('alex.demo@example.test');
  await demo.getByLabel('Mật khẩu', { exact: true }).fill('YourSafe-Demo-Only!');
  await demo.locator('body').evaluate(() => document.fonts.ready);
  if (errors.length) throw new Error(errors.join('; '));
  const capture = path.join(root, 'website/assets/captures-inbox/08-extension-login-context.png');
  await page.screenshot({ path: capture });
  // Append PNG provenance without changing its pixels or needing an image library.
  const provenance = Buffer.from('prompt\0Origin: actual website/demo-login.html and browser-extension popup source rendered in Edge by Capture-WebsiteExtension.cjs --login-context; synthetic transport and credentials; not live Native Messaging evidence.', 'latin1');
  const chunk = Buffer.concat([Buffer.from('tEXt'), provenance]);
  let crc = 0xffffffff;
  for (const byte of chunk) {
    crc ^= byte;
    for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ (crc & 1 ? 0xedb88320 : 0);
  }
  const length = Buffer.alloc(4), checksum = Buffer.alloc(4);
  length.writeUInt32BE(provenance.length);
  checksum.writeUInt32BE((crc ^ 0xffffffff) >>> 0);
  const png = fs.readFileSync(capture);
  fs.writeFileSync(capture, Buffer.concat([png.subarray(0, -12), length, chunk, checksum, png.subarray(-12)]));
  console.log('Captured 08: actual demo page and popup source, synthetic transport, no real vault.');
  await page.close();
}

(async () => {
  const browser = await chromium.launch({channel:'msedge',headless:true});
  try {
    if (process.argv.includes('--login-context')) { await captureLoginContext(browser); return; }
    const page = await browser.newPage({viewport:{width:360,height:600},deviceScaleFactor:2,colorScheme:'light'});
    await page.addInitScript(() => {
      const listeners = [];
      window.chrome = {runtime:{connect:() => ({onMessage:{addListener: fn => listeners.push(fn)},onDisconnect:{addListener:()=>{}},disconnect:()=>{},postMessage: envelope => {
        const result = envelope.message.action === 'resume' ? {resumed:false} : envelope.message.action === 'discover' ? {token:'demo-only',origin:'https://github.com',truncated:false,accounts:[
          {id:'11111111-1111-1111-1111-111111111111',title:'GitHub · Personal',username:'alex.demo@example.test',hasPassword:true,hasTotp:true},
          {id:'22222222-2222-2222-2222-222222222222',title:'GitHub · Work',username:'work.demo@example.test',hasPassword:true,hasTotp:false}
        ]} : {code:'123456',periodSeconds:30,expiresAtUnixMs:Date.now()+30000};
        queueMicrotask(() => listeners.forEach(fn=>fn({requestId:envelope.requestId,ok:true,result})));
      }})}};
    });
    page.on('pageerror', error => console.error(error.message));
    await page.route('https://preview.example.test/**', route => route.fulfill({contentType:'text/html',body:html}));
    await page.goto('https://preview.example.test/');
    await page.addScriptTag({content:script});
    await page.locator('.account').first().waitFor();
    fs.mkdirSync(output,{recursive:true});
    await page.locator('body').screenshot({path:path.join(output,'extension-accounts.png')});
    fs.copyFileSync(path.join(output,'extension-accounts.png'),path.join(root,'website/assets/captures-inbox/03-extension-accounts.png'));
    await page.getByRole('button',{name:'View TOTP for GitHub · Personal',exact:true}).click();
    await page.waitForFunction(()=>document.getElementById('totp-code').textContent==='123 456');
    await page.locator('body').screenshot({path:path.join(output,'extension-code.png')});
    fs.copyFileSync(path.join(output,'extension-code.png'),path.join(root,'website/assets/captures-inbox/07-extension-totp.png'));
    console.log('Captured actual extension markup/script/CSS with demo-only transport fixtures.');
  } finally {await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
