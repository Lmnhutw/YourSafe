"use strict";

(() => {
  if (window.yourSafeAnalyticsInitialized) return;
  window.yourSafeAnalyticsInitialized = true;

  window.dataLayer = window.dataLayer || [];
  window.gtag = window.gtag || function () { window.dataLayer.push(arguments); };
  window.gtag("js", new Date());
  window.gtag("config", "G-1GP0TS974S", {
    page_location: location.origin + location.pathname,
    page_referrer: document.referrer ? new URL(document.referrer).origin : "",
    allow_google_signals: false,
    allow_ad_personalization_signals: false,
  });

  function trackClick(event) {
    if (!event.isTrusted || event.defaultPrevented || event.button > 1) return;
    const link = event.target.closest("a[data-analytics-event]");
    if (!link) return;

    const name = link.dataset.analyticsEvent;
    const parameters = { location: link.dataset.analyticsLocation };
    if (name === "download_click") parameters.platform = "windows";
    if (name === "extension_click") parameters.browser = "chrome_edge_brave";
    window.gtag("event", name, parameters);
  }

  document.addEventListener("click", trackClick);
  document.addEventListener("auxclick", trackClick);
})();
