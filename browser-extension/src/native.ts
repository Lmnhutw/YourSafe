import { isRequest, isResponse, maxFrameBytes, type Action, type Request } from './protocol';
declare const NATIVE_HOST: string;

// One connection per popup interaction; queued operations are never retried.
export function nativeSession(onFailure: (error: string) => void): {
  request(action: Action, payload?: Request['payload']): Promise<unknown>;
  close(error?: string): void;
} {
  let port: chrome.runtime.Port | undefined = chrome.runtime.connectNative(NATIVE_HOST);
  let pending: { request: Request; resolve(value: unknown): void; reject(error: Error): void } | undefined;
  let timeout: ReturnType<typeof setTimeout> | undefined;
  let queue = Promise.resolve();
  const close = (error = 'desktopUnavailable') => {
    const connection = port;
    port = undefined;
    clearTimeout(timeout);
    timeout = undefined;
    const operation = pending;
    pending = undefined;
    operation?.reject(new Error(error));
    connection?.onMessage.removeListener(receive);
    connection?.onDisconnect.removeListener(disconnected);
    connection?.disconnect();
  };
  const fail = (error: string) => { if (port) { close(error); onFailure(error); } };
  const disconnected = () => { void chrome.runtime.lastError; fail('desktopUnavailable'); };
  const receive = (value: unknown) => {
    const operation = pending;
    if (!port) return;
    if (!operation || new TextEncoder().encode(JSON.stringify(value)).length > maxFrameBytes || !isResponse(value, operation.request)) {
      fail('invalidRequest'); return;
    }
    if (!value.ok) { fail(value.error ?? 'unavailable'); return; }
    clearTimeout(timeout);
    timeout = undefined;
    pending = undefined;
    operation.resolve(value.result);
  };
  port.onDisconnect.addListener(disconnected);
  port.onMessage.addListener(receive);
  return {
    request(action, payload = {}) {
      const request: Request = { version: 2, requestId: crypto.randomUUID(), action, payload };
      if (!isRequest(request)) return Promise.reject(new Error('invalidRequest'));
      const result = queue.then(() => new Promise<unknown>((resolve, reject) => {
        if (!port) { reject(new Error('desktopUnavailable')); return; }
        pending = { request, resolve, reject };
        timeout = setTimeout(() => fail('desktopUnavailable'), 10000);
        try { port.postMessage(request); } catch { fail('desktopUnavailable'); }
      }));
      queue = result.then(() => {}, () => {});
      return result;
    },
    close
  };
}
