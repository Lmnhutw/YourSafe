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
(async () => {
  const browser = await chromium.launch({channel:'msedge',headless:true});
  try {
    const page = await browser.newPage({viewport:{width:360,height:600},deviceScaleFactor:2,colorScheme:'light'});
    await page.addInitScript(() => {
      const listeners = [];
      window.chrome = {runtime:{connect:() => ({onMessage:{addListener: fn => listeners.push(fn)},onDisconnect:{addListener:()=>{}},disconnect:()=>{},postMessage: envelope => {
        const result = envelope.message.action === 'discover' ? {token:'demo-only',origin:'https://github.com',truncated:false,accounts:[
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
