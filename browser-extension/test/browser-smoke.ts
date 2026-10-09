import { findFields, visibleFields, fillFields } from '../src/fields';
let checks = 0;
function check(value: boolean, label: string): void { if (!value) throw new Error(label); checks++; }
async function run(): Promise<void> {
const fixture = document.getElementById('fixture');
const results = document.getElementById('results');
if (!fixture || !results) throw new Error('Missing test layout');
try {
  fixture.innerHTML = '<form><input type="email" autocomplete="username"><input type="password" autocomplete="current-password"><button>Sign in</button></form>';
  const fields = findFields();
  check(!!fields?.username, 'email and password detected');
  let input = 0, change = 0, submit = 0;
  fixture.addEventListener('input', () => input++);
  fixture.addEventListener('change', () => change++);
  fixture.addEventListener('submit', () => submit++);
  if (!fields) throw new Error('Missing fields');
  await fillFields(fields, 'synthetic@example.test', 'synthetic-password');
  check(fields.username?.value === 'synthetic@example.test' && fields.password.value === 'synthetic-password', 'native setters fill');
  check(input === 2 && change === 2 && submit === 0, 'events emitted without submit');
  fixture.innerHTML = '<input type="password" autocomplete="new-password">';
  check(!findFields(), 'new password excluded');
  fixture.innerHTML = '<input type="password" disabled><input type="password" readonly><input type="password" hidden>';
  check(!findFields(), 'noneditable/hidden inputs excluded');
  fixture.innerHTML = '<form><input type="password"></form><form><input type="password"></form>';
  check(!findFields(), 'ambiguous passwords rejected');
  const focused = fixture.querySelector('input');
  check(focused instanceof HTMLInputElement && findFields(focused)?.password === focused, 'focus resolves ambiguity');
  fixture.innerHTML = '<form><input type="password"></form>';
  check(!!findFields() && !findFields()?.username, 'password-only detected');
  fixture.innerHTML = '<form><input type="text"><input type="text"><input type="password"></form>';
  check(!findFields(), 'ambiguous usernames rejected');
  const chosen = fixture.querySelector('input');
  if (!(chosen instanceof HTMLInputElement)) throw new Error('Missing username');
  const selected = findFields(chosen);
  if (!selected) throw new Error('Focus must resolve username');
  await fillFields(selected, 'chosen-user', 'chosen-password');
  check(chosen.value === 'chosen-user' && selected.password.value === 'chosen-password', 'focused username survives fill revalidation');
  fixture.innerHTML = '<form><input type="text" autocomplete="username"><input type="password"><input type="password"></form>';
  const chosenPassword = fixture.querySelector('input[type=password]');
  if (!(chosenPassword instanceof HTMLInputElement)) throw new Error('Missing password');
  const passwordSelection = findFields(chosenPassword);
  if (!passwordSelection) throw new Error('Focus must resolve password');
  await fillFields(passwordSelection, 'user', 'chosen-password');
  check(chosenPassword.value === 'chosen-password', 'focused password survives username revalidation');
  fixture.innerHTML = '<form><fieldset disabled><input type="text"><input type="password"></fieldset></form>';
  check(!findFields(), 'fieldset disabled is inherited');
  fixture.innerHTML = '<form><fieldset disabled><legend><input type="password"></legend><input type="password"></fieldset></form>';
  check(!!findFields(), 'first legend retains native enabled semantics');
  fixture.innerHTML = '';
  fixture.insertAdjacentHTML('beforeend', '<form><input type="text" autocomplete="username"><input type="password"></form>');
  check(!!findFields()?.username, 'dynamic form detected');
  const dynamic = findFields();
  if (!dynamic?.username) throw new Error('Missing dynamic fields');
  dynamic.username.addEventListener('input', () => { dynamic.password.autocomplete = 'new-password'; }, { once: true });
  let rejected = false;
  try { await fillFields(dynamic, 'test', 'must-not-fill'); } catch { rejected = true; }
  check(rejected && dynamic.password.value === '', 'reclassified password rejected after username events');
  fixture.innerHTML = '<form action="https://untrusted.example/login"><input type="password"></form>';
  check(!findFields(), 'cross-origin form action rejected');
  fixture.innerHTML = '<form action="http://untrusted.example/login"><input type="password"></form>';
  check(!findFields(), 'non-loopback HTTP form action rejected');
  fixture.innerHTML = '<form><input type="password"><button formaction="https://untrusted.example/login">Submit</button></form>';
  check(!findFields(), 'submit button cross-origin override rejected');
  fixture.innerHTML = '<form id="login"><input type="password"></form><button form="login" formaction="https://untrusted.example/login">Submit</button>';
  check(!findFields(), 'external submit button override rejected');
  fixture.innerHTML = '<form><input autocomplete="username"><input type="password"></form>';
  const changedAction = findFields();
  if (!changedAction?.username || !changedAction.password.form) throw new Error('Missing action-change fixture');
  changedAction.username.addEventListener('input', () => { if (changedAction.password.form) changedAction.password.form.action = 'https://untrusted.example/collect'; });
  rejected = false;
  try { await fillFields(changedAction, 'synthetic', 'must-not-fill'); } catch { rejected = true; }
  check(rejected && !changedAction.password.value, 'changed form action rejected before password disclosure');
  for (const hiddenStyle of ['opacity:0', 'opacity:0.01', 'filter:opacity(0)', 'filter:opacity(1%)', 'position:fixed;left:-2000px', 'content-visibility:hidden']) {
    fixture.innerHTML = `<form style="${hiddenStyle}"><input type="password"></form>`;
    check(!findFields(), 'invisible/offscreen form rejected: ' + hiddenStyle);
  }
  fixture.innerHTML = '<form style="opacity:.2"><div style="opacity:.2"><input type="password"></div></form>';
  check(!findFields(), 'composed ancestor opacity rejected');
  fixture.innerHTML = '<form><input type="password"></form>';
  const covered = fixture.querySelector('input');
  if (!(covered instanceof HTMLInputElement)) throw new Error('Missing covered field');
  const bounds = covered.getBoundingClientRect();
  const overlay = document.createElement('div');
  overlay.style.cssText = `position:fixed;left:${bounds.left}px;top:${bounds.top}px;width:${bounds.width}px;height:${bounds.height}px;background:white;z-index:10`;
  fixture.append(overlay);
  check(!findFields(), 'occluded field rejected');
  overlay.style.pointerEvents = 'none';
  check(!await visibleFields({ password: covered }), 'paint visibility rejects a pointer-events:none cover');
  overlay.remove();
  check(!!findFields(), 'visible field becomes eligible after overlay removal');
  const afterCover = findFields();
  check(!!afterCover && await visibleFields(afterCover), 'visibility tracker accepts the uncovered field');
  results.textContent = `PASS: ${checks} browser detection/fill checks`;
} catch (error) { results.textContent = 'FAIL: ' + (error instanceof Error ? error.message : 'Unknown error'); throw error; }
}
void run();
