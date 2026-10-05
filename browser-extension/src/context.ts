export type Target = { tabId: number; windowId: number; frameId: 0; documentId: string; url: string; origin: string };
export function sameTarget(first: Target, second: Target): boolean {
  return first.tabId === second.tabId && first.windowId === second.windowId && first.frameId === second.frameId
    && first.documentId === second.documentId && first.url === second.url && first.origin === second.origin;
}
export function isPopup(sender: chrome.runtime.MessageSender): boolean {
  return sender.id === chrome.runtime.id && !sender.tab && sender.url === chrome.runtime.getURL('popup.html');
}
