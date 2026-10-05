import { build } from 'esbuild';
import { mkdir, copyFile, writeFile, readFile } from 'node:fs/promises';
const production = process.argv.includes('--production');
const output = production ? 'dist/production' : 'dist/development';
await mkdir(output, { recursive: true });
await build({
  entryPoints: ['src/worker.ts', 'src/content.ts', 'src/popup.ts'],
  bundle: true, outdir: output, format: 'iife', target: 'chrome127',
  define: { NATIVE_HOST: JSON.stringify(production ? 'com.yoursafe.autofill' : 'com.yoursafe.autofill.dev') }
});
for (const file of ['popup.html', 'popup.css']) await copyFile(`static/${file}`, `${output}/${file}`);
await writeFile(`${output}/manifest.json`, JSON.stringify({
  manifest_version: 3, name: production ? 'YourSafe' : 'YourSafe Development', version: '1.0.0',
  minimum_chrome_version: '127',
  ...(!production ? { key: (await readFile('development-key.txt', 'utf8')).trim() } : {}),
  permissions: ['nativeMessaging', 'webNavigation'],
  host_permissions: ['https://*/*'],
  background: { service_worker: 'worker.js' },
  action: { default_title: 'YourSafe', default_popup: 'popup.html' },
  content_scripts: [{ matches: ['https://*/*'], js: ['content.js'], all_frames: false, run_at: 'document_idle' }],
  content_security_policy: { extension_pages: "script-src 'self'; object-src 'none'; frame-ancestors 'none'" }
}, null, 2));
