export type Credential = { id: string; title: string; username: string; hasPassword: boolean; hasTotp: boolean };
export type Discovery = { credentials: Credential[]; truncated: boolean };
export type Secret = { username: string; password: string };
export type Totp = { code: string; periodSeconds: number; expiresAtUnixMs: number };
export type Action = 'ping' | 'getStatus' | 'findCredentials' | 'getCredentialSecret' | 'getCredentialTotp' | 'copyCredentialTotp' | 'showApp';
export type Request = { version: 2; requestId: string; action: Action; payload: { origin?: string; credentialId?: string } };
export type Response = { version: 2; requestId: string; ok: boolean; result?: unknown; error?: string };
export const maxFrameBytes = 65536;
// ponytail: autofill supports public HTTPS and loopback HTTP only; add other origins only with an explicit trust policy.
const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
export function record(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
function only(value: Record<string, unknown>, keys: string[]): boolean {
  return Object.keys(value).every(key => keys.includes(key));
}
export function canonicalOrigin(value: string): string | undefined {
  if (value.length > 4096 || /[\\\s\p{Cc}]/u.test(value) || !/^https?:\/\//i.test(value)) return;
  try {
    const url = new URL(value);
    const localHttp = url.protocol === 'http:' && ['localhost', '127.0.0.1', '[::1]'].includes(url.hostname);
    if ((url.protocol !== 'https:' && !localHttp) || url.username || url.password || !url.hostname) return;
    if (value.slice(value.indexOf('//') + 2).split(/[/?#]/)[0]?.includes('%')) return;
    // Do not accept shortened/hex/octal numeric IP spellings.
    if (/^\d+\.\d+\.\d+\.\d+$/.test(url.hostname)) {
      const authority = value.slice(value.indexOf('//') + 2).split(/[/?#]/)[0]?.split(':')[0];
      if (authority !== url.hostname) return;
    }
    return url.origin;
  } catch { return; }
}
export function isRequest(value: unknown): value is Request {
  if (!record(value) || !only(value, ['version', 'requestId', 'action', 'payload'])
    || value.version !== 2 || typeof value.requestId !== 'string' || !guid.test(value.requestId)
    || !record(value.payload) || !only(value.payload, ['origin', 'credentialId'])) return false;
  const { origin, credentialId } = value.payload;
  switch (value.action) {
    case 'ping': case 'getStatus': case 'showApp': return origin === undefined && credentialId === undefined;
    case 'findCredentials': return typeof origin === 'string' && canonicalOrigin(origin) === origin && credentialId === undefined;
    case 'getCredentialSecret': case 'getCredentialTotp': case 'copyCredentialTotp':
      return typeof origin === 'string' && canonicalOrigin(origin) === origin
      && typeof credentialId === 'string' && guid.test(credentialId) && credentialId !== '00000000-0000-0000-0000-000000000000';
    default: return false;
  }
}
export function isCredential(value: unknown): value is Credential {
  return record(value) && only(value, ['id', 'title', 'username', 'hasPassword', 'hasTotp']) && typeof value.id === 'string' && guid.test(value.id)
    && value.id !== '00000000-0000-0000-0000-000000000000'
    && typeof value.title === 'string' && typeof value.username === 'string'
    && typeof value.hasPassword === 'boolean' && typeof value.hasTotp === 'boolean' && (value.hasPassword || value.hasTotp);
}
export function isSecret(value: unknown): value is Secret {
  return record(value) && only(value, ['username', 'password']) && typeof value.username === 'string' && typeof value.password === 'string';
}
export function isDiscovery(value: unknown): value is Discovery {
  return record(value) && only(value, ['credentials', 'truncated']) && Array.isArray(value.credentials)
    && value.credentials.every(isCredential) && typeof value.truncated === 'boolean';
}
export function isTotp(value: unknown): value is Totp {
  return record(value) && only(value, ['code', 'periodSeconds', 'expiresAtUnixMs'])
    && typeof value.code === 'string' && /^(?:[0-9]{6}|[0-9]{8})$/.test(value.code)
    && typeof value.periodSeconds === 'number' && Number.isInteger(value.periodSeconds) && value.periodSeconds > 0 && value.periodSeconds <= 2147483647
    && typeof value.expiresAtUnixMs === 'number' && Number.isSafeInteger(value.expiresAtUnixMs)
    && value.expiresAtUnixMs > 0 && value.expiresAtUnixMs <= 253402300799999;
}
export function isResponse(value: unknown, request: Request): value is Response {
  if (!record(value) || !only(value, ['version', 'requestId', 'ok', 'result', 'error'])
    || value.version !== 2 || value.requestId !== request.requestId || typeof value.ok !== 'boolean') return false;
  if (!value.ok) return value.result === undefined && typeof value.error === 'string'
    && ['locked', 'unavailable', 'desktopUnavailable', 'invalidRequest'].includes(value.error);
  if (value.error !== undefined) return false;
  switch (request.action) {
    case 'findCredentials': return isDiscovery(value.result);
    case 'getCredentialSecret': return isSecret(value.result);
    case 'getCredentialTotp': return isTotp(value.result);
    case 'copyCredentialTotp': return record(value.result) && only(value.result, ['copied']) && value.result.copied === true;
    case 'getStatus': return record(value.result) && only(value.result, ['unlocked']) && typeof value.result.unlocked === 'boolean';
    case 'ping': return record(value.result) && only(value.result, ['host']) && value.result.host === 'YourSafe';
    case 'showApp': return record(value.result) && only(value.result, ['shown']) && typeof value.result.shown === 'boolean';
  }
}
