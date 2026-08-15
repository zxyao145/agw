const DEFAULT_DESKTOP_CHAT_HREF = "/desktop/chat/";
const DESKTOP_APP_ORIGIN = "https://desktop.agw.local";

export function getDesktopChatReturnHref(returnTo: string | null): string {
  if (!returnTo) return DEFAULT_DESKTOP_CHAT_HREF;

  try {
    const url = new URL(returnTo, DESKTOP_APP_ORIGIN);
    const normalizedPathname = url.pathname.replace(/\/+$/u, "");
    if (url.origin !== DESKTOP_APP_ORIGIN || normalizedPathname !== "/desktop/chat") {
      return DEFAULT_DESKTOP_CHAT_HREF;
    }

    return `${url.pathname}${url.search}`;
  } catch {
    return DEFAULT_DESKTOP_CHAT_HREF;
  }
}

export function buildSettingsHref(settingsHref: string, chatReturnHref: string): string {
  const hashIndex = settingsHref.indexOf("#");
  const hash = hashIndex >= 0 ? settingsHref.slice(hashIndex) : "";
  const hrefWithoutHash = hashIndex >= 0 ? settingsHref.slice(0, hashIndex) : settingsHref;
  const queryIndex = hrefWithoutHash.indexOf("?");
  const pathname = queryIndex >= 0 ? hrefWithoutHash.slice(0, queryIndex) : hrefWithoutHash;
  const search = queryIndex >= 0 ? hrefWithoutHash.slice(queryIndex + 1) : "";
  const searchParams = new URLSearchParams(search);
  searchParams.set("returnTo", getDesktopChatReturnHref(chatReturnHref));

  return `${pathname}?${searchParams.toString()}${hash}`;
}
