import { build } from 'esbuild';
import { mkdir, copyFile, writeFile, readFile } from 'node:fs/promises';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
const execFileAsync = promisify(execFile);
const production = process.argv.includes('--production');
const output = production ? 'dist/production' : 'dist/development';
const sites = ['https://*/*', 'http://localhost/*', 'http://127.0.0.1/*', 'http://[::1]/*'];
await mkdir(`${output}/icons`, { recursive: true });
await build({
  entryPoints: ['src/worker.ts', 'src/content.ts', 'src/popup.ts'],
  bundle: true, outdir: output, format: 'iife', target: 'chrome127',
  define: { NATIVE_HOST: JSON.stringify(production ? 'com.yoursafe.autofill' : 'com.yoursafe.autofill.dev') }
});
for (const file of ['popup.html', 'popup.css']) await copyFile(`static/${file}`, `${output}/${file}`);
await copyFile('../website/assets/app-logo.png', `${output}/icons/app-logo.png`);
for (const size of [16, 32, 48, 128]) {
  await execFileAsync('powershell.exe', ['-NoProfile', '-Command', `Add-Type -AssemblyName System.Drawing; $image=[System.Drawing.Image]::FromFile((Resolve-Path '${output}/icons/app-logo.png')); try { $bitmap=[System.Drawing.Bitmap]::new(${size},${size}); try { $graphics=[System.Drawing.Graphics]::FromImage($bitmap); try { $graphics.InterpolationMode=[System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic; $graphics.DrawImage($image,0,0,${size},${size}); $bitmap.Save((Join-Path (Get-Location) '${output}/icons/icon-${size}.png'),[System.Drawing.Imaging.ImageFormat]::Png) } finally { $graphics.Dispose() } } finally { $bitmap.Dispose() } } finally { $image.Dispose() }`]);
}
await writeFile(`${output}/manifest.json`, JSON.stringify({
  manifest_version: 3, name: production ? 'YourSafe' : 'YourSafe Development', version: '1.0.0',
  minimum_chrome_version: '127',
  ...(!production ? { key: (await readFile('development-key.txt', 'utf8')).trim() } : {}),
  permissions: ['nativeMessaging', 'webNavigation'],
  host_permissions: sites,
  background: { service_worker: 'worker.js' },
  icons: { 16: 'icons/icon-16.png', 32: 'icons/icon-32.png', 48: 'icons/icon-48.png', 128: 'icons/icon-128.png' },
  action: { default_title: 'YourSafe', default_popup: 'popup.html', default_icon: { 16: 'icons/icon-16.png', 32: 'icons/icon-32.png' } },
  content_scripts: [{ matches: sites, js: ['content.js'], all_frames: false, run_at: 'document_idle' }],
  content_security_policy: { extension_pages: "script-src 'self'; object-src 'none'; frame-ancestors 'none'" }
}, null, 2));
