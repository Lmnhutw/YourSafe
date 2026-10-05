import { isRequest, isResponse, maxFrameBytes, type Action, type Request } from './protocol';
declare const NATIVE_HOST: string;

// A separate native connection per request keeps timeout/disconnect handling bounded; secrets are never retried.
export function nativeRequest(action: Action, payload: Request['payload'] = {}): Promise<unknown> {
  const request: Request = { version: 1, requestId: crypto.randomUUID(), action, payload };
  if (!isRequest(request)) return Promise.reject(new Error('invalidRequest'));
  return new Promise((resolve, reject) => {
    const port = chrome.runtime.connectNative(NATIVE_HOST);
    let finished = false;
    const finish = (result: unknown, error?: string) => {
      if (finished) return;
      finished = true;
      clearTimeout(timeout);
      port.disconnect();
      if (error) reject(new Error(error)); else resolve(result);
    };
    const timeout = setTimeout(() => finish(undefined, 'desktopUnavailable'), 10000);
    port.onDisconnect.addListener(() => { void chrome.runtime.lastError; finish(undefined, 'desktopUnavailable'); });
    port.onMessage.addListener((value: unknown) => {
      if (new TextEncoder().encode(JSON.stringify(value)).length > maxFrameBytes || !isResponse(value, request)) {
        finish(undefined, 'invalidRequest'); return;
      }
      if (!value.ok) finish(undefined, value.error); else finish(value.result);
    });
    port.postMessage(request);
  });
}
