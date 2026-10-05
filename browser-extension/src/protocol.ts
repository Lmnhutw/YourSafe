export type Credential = { id: string; title: string; username: string };
export type Discovery = { credentials: Credential[]; truncated: boolean };
export type Secret = { username: string; password: string };
export type Action = 'ping' | 'getStatus' | 'findCredentials' | 'getCredentialSecret' | 'showApp';
export type Request = { version: 1; requestId: string; action: Action; payload: { origin?: string; credentialId?: string } };
export type Response = { version: 1; requestId: string; ok: boolean; result?: unknown; error?: string };
export const maxFrameBytes = 65536;
const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
export function record(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
function only(value: Record<string, unknown>, keys: string[]): boolean {
  return Object.keys(value).every(key => keys.includes(key));
}
export function canonicalOrigin(value: string): string | undefined {
  if (value.length > 4096 || /[\\\s\p{Cc}]/u.test(value) || !/^https:\/\//i.test(value)
    || value.slice(8).split(/[/?#]/)[0]?.includes('%')) return;
  try {
    const url = new URL(value);
    if (url.protocol !== 'https:' || url.username || url.password || !url.hostname) return;
    // Do not accept shortened/hex/octal numeric IP spellings.
    if (/^\d+\.\d+\.\d+\.\d+$/.test(url.hostname)) {
      const authority = value.slice(8).split(/[/?#]/)[0]?.split(':')[0];
      if (authority !== url.hostname) return;
    }
    return url.origin;
  } catch { return; }
}
export function isRequest(value: unknown): value is Request {
  if (!record(value) || !only(value, ['version', 'requestId', 'action', 'payload'])
    || value.version !== 1 || typeof value.requestId !== 'string' || !guid.test(value.requestId)
    || !record(value.payload) || !only(value.payload, ['origin', 'credentialId'])) return false;
  const { origin, credentialId } = value.payload;
  switch (value.action) {
    case 'ping': case 'getStatus': case 'showApp': return origin === undefined && credentialId === undefined;
    case 'findCredentials': return typeof origin === 'string' && canonicalOrigin(origin) === origin && credentialId === undefined;
    case 'getCredentialSecret': return typeof origin === 'string' && canonicalOrigin(origin) === origin
      && typeof credentialId === 'string' && guid.test(credentialId) && credentialId !== '00000000-0000-0000-0000-000000000000';
    default: return false;
  }
}
export function isCredential(value: unknown): value is Credential {
  return record(value) && only(value, ['id', 'title', 'username']) && typeof value.id === 'string' && guid.test(value.id)
    && value.id !== '00000000-0000-0000-0000-000000000000'
    && typeof value.title === 'string' && typeof value.username === 'string';
}
export function isSecret(value: unknown): value is Secret {
  return record(value) && only(value, ['username', 'password']) && typeof value.username === 'string' && typeof value.password === 'string';
}
export function isDiscovery(value: unknown): value is Discovery {
  return record(value) && only(value, ['credentials', 'truncated']) && Array.isArray(value.credentials)
    && value.credentials.every(isCredential) && typeof value.truncated === 'boolean';
}
export function isResponse(value: unknown, request: Request): value is Response {
  if (!record(value) || !only(value, ['version', 'requestId', 'ok', 'result', 'error'])
    || value.version !== 1 || value.requestId !== request.requestId || typeof value.ok !== 'boolean') return false;
  if (!value.ok) return value.result === undefined && typeof value.error === 'string'
    && ['locked', 'unavailable', 'desktopUnavailable', 'invalidRequest'].includes(value.error);
  if (value.error !== undefined) return false;
  switch (request.action) {
    case 'findCredentials': return isDiscovery(value.result);
    case 'getCredentialSecret': return isSecret(value.result);
    case 'getStatus': return record(value.result) && only(value.result, ['unlocked']) && typeof value.result.unlocked === 'boolean';
    case 'ping': return record(value.result) && only(value.result, ['host']) && value.result.host === 'YourSafe';
    case 'showApp': return record(value.result) && only(value.result, ['shown']) && typeof value.result.shown === 'boolean';
  }
}
