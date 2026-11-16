"use strict";

const downloadControls = [...document.querySelectorAll('[data-download-control]')];
const installerUrl = document.querySelector('[data-download-link]').href;
const releasesUrl = 'https://github.com/Lmnhutw/YourSafe/releases/latest';
const englishDownloads = document.documentElement.lang === 'en';
let selectedPackage = 'installer';
let portableUrl;
let portableFileName;
let portableRequest;
let portableState = 'idle';

function updateDownloads() {
  const portable = selectedPackage === 'portable';
  let label = englishDownloads ? 'Download for Windows' : 'Tải cho Windows';
  if (portable && portableUrl) label = englishDownloads ? 'Download ZIP' : 'Tải bản ZIP';
  if (portable && !portableUrl) label = englishDownloads ? 'Find ZIP on GitHub' : 'Xem bản ZIP trên GitHub';
  for (const link of document.querySelectorAll('[data-download-link]')) {
    link.href = portable ? portableUrl || releasesUrl : installerUrl;
    link.querySelector('span').textContent = label;
  }
  for (const description of document.querySelectorAll('[data-download-description]')) {
    description.textContent = portable
      ? description.dataset.portableDescription.replace('{filename}', portableFileName || 'ZIP')
      : description.dataset.installerDescription;
    if (portable && portableState === 'loading') description.textContent += englishDownloads ? ' Finding the latest ZIP…' : ' Đang tìm bản ZIP mới nhất…';
    if (portable && portableState === 'unavailable') description.textContent += englishDownloads ? ' Open the release page to choose the ZIP file.' : ' Mở trang phát hành để chọn tệp ZIP.';
  }
  for (const option of document.querySelectorAll('[data-download-package]')) {
    option.setAttribute('aria-pressed', String(option.dataset.downloadPackage === selectedPackage));
  }
}

async function findPortableDownload() {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 8000);
  try {
    const response = await fetch('https://api.github.com/repos/Lmnhutw/YourSafe/releases/latest', {
      signal: controller.signal, credentials: 'omit', referrerPolicy: 'no-referrer',
    });
    if (!response.ok) throw new Error('Release unavailable');
    const release = await response.json();
    if (!Array.isArray(release?.assets)) throw new Error('Missing release assets');
    const asset = release.assets.find(item => item && typeof item.name === 'string'
      && /^YourSafe-\d[\w.+-]*-win-x64\.zip$/.test(item.name)
      && typeof item.browser_download_url === 'string');
    if (!asset) throw new Error('Missing Windows ZIP');
    const url = new URL(asset.browser_download_url);
    if (url.origin !== 'https://github.com' || url.username || url.password || url.search || url.hash
      || !/^\/Lmnhutw\/YourSafe\/releases\/download\/[^/]+\/[^/]+$/.test(url.pathname)
      || !url.pathname.endsWith(`/${asset.name}`)) throw new Error('Invalid download URL');
    portableUrl = url.href;
    portableFileName = asset.name;
    portableState = 'ready';
  } catch {
    portableState = 'unavailable';
  } finally {
    clearTimeout(timeout);
    portableRequest = undefined;
    updateDownloads();
  }
}

function closeDownloadMenu(control, restoreFocus = false) {
  control.querySelector('.download-menu').hidden = true;
  const toggle = control.querySelector('.download-toggle');
  toggle.setAttribute('aria-expanded', 'false');
  if (restoreFocus) toggle.focus({ preventScroll: true });
}

function openDownloadMenu(control) {
  downloadControls.forEach(item => closeDownloadMenu(item));
  control.querySelector('.download-menu').hidden = false;
  control.querySelector('.download-toggle').setAttribute('aria-expanded', 'true');
  control.querySelector('[aria-pressed="true"]').focus({ preventScroll: true });
}

for (const control of downloadControls) {
  const toggle = control.querySelector('.download-toggle');
  control.classList.add('is-enhanced');
  toggle.hidden = false;
  toggle.addEventListener('click', () => {
    if (toggle.getAttribute('aria-expanded') === 'true') closeDownloadMenu(control);
    else openDownloadMenu(control);
  });
  toggle.addEventListener('keydown', event => {
    if (event.key === 'ArrowDown') { event.preventDefault(); openDownloadMenu(control); }
  });
  control.addEventListener('keydown', event => {
    if (event.key === 'Escape' && toggle.getAttribute('aria-expanded') === 'true') {
      event.stopPropagation();
      closeDownloadMenu(control, true);
    }
  });
  control.addEventListener('focusout', event => {
    if (!control.contains(event.relatedTarget)) closeDownloadMenu(control);
  });
  control.querySelectorAll('[data-download-package]').forEach(option => option.addEventListener('click', () => {
    const next = option.dataset.downloadPackage;
    if (next !== 'installer' && next !== 'portable') return;
    selectedPackage = next;
    if (next === 'portable' && !portableUrl && !portableRequest) {
      portableState = 'loading';
      portableRequest = findPortableDownload();
    }
    updateDownloads();
    closeDownloadMenu(control, true);
  }));
}
document.addEventListener('pointerdown', event => {
  downloadControls.forEach(control => { if (!control.contains(event.target)) closeDownloadMenu(control); });
});

if (
  "IntersectionObserver" in window &&
  !window.matchMedia("(prefers-reduced-motion: reduce)").matches
) {
  const observer = new IntersectionObserver(
    (entries) => {
      for (const entry of entries) {
        if (entry.isIntersecting) {
          entry.target.classList.add("is-revealed");
          observer.unobserve(entry.target);
        }
      }
    },
    { threshold: 0.2 },
  );
  document
    .querySelectorAll("[data-reveal]")
    .forEach((element) => observer.observe(element));
}

const showcase = document.querySelector(".showcase");
const slides = [...showcase.querySelectorAll(".showcase-slide")];
const selectors = [...showcase.querySelectorAll("[data-slide]")];
let currentSlide = 0;
const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)");
let autoplayTimer;
let slideAnimations = [];

function finishSlideTransition() {
  slideAnimations.forEach(animation => animation.cancel());
  slideAnimations = [];
  slides.forEach((slide, index) => { slide.hidden = index !== currentSlide; });
}

function updateAutoplay() {
  window.clearInterval(autoplayTimer);
  if (reducedMotion.matches || document.hidden || showcase.matches(":hover") || showcase.contains(document.activeElement)) return;
  autoplayTimer = window.setInterval(() => showSlide(currentSlide + 1, false), 5000);
}

for (const event of ["mouseenter", "mouseleave", "focusin", "focusout"]) {
  showcase.addEventListener(event, updateAutoplay);
}
document.addEventListener("visibilitychange", updateAutoplay);
reducedMotion.addEventListener("change", () => {
  finishSlideTransition();
  updateAutoplay();
});

function showSlide(index, announce = true) {
  const previousSlide = currentSlide;
  finishSlideTransition();
  currentSlide = (index + slides.length) % slides.length;
  slides.forEach((slide, position) => {
    slide.hidden = position !== currentSlide;
    slide.inert = position !== currentSlide;
    slide.setAttribute("aria-hidden", String(position !== currentSlide));
    selectors[position].setAttribute("aria-current", String(position === currentSlide));
  });
  if (currentSlide !== previousSlide) closePreview();
  if (currentSlide !== previousSlide && !reducedMotion.matches) {
    const previous = slides[previousSlide];
    const current = slides[currentSlide];
    previous.hidden = false;
    const timing = { duration: 400, easing: "ease-in-out" };
    slideAnimations = [
      previous.animate([{ opacity: 1 }, { opacity: 0 }], timing),
      current.animate([{ opacity: 0 }, { opacity: 1 }], timing),
    ];
    slideAnimations[0].onfinish = finishSlideTransition;
  }
  if (announce) {
    showcase.querySelector(".showcase-status").textContent =
      `${currentSlide + 1} / ${slides.length}: ${slides[currentSlide].querySelector("h3").textContent}`;
  }
  updateAutoplay();
}

// Without JavaScript, all three articles remain visible and the controls stay hidden.
showcase.classList.add("is-enhanced");
showcase.tabIndex = 0;
showcase.querySelector(".showcase-controls").hidden = false;
showSlide(0, false);
selectors.forEach((button, index) => button.addEventListener("click", () => showSlide(index)));
showcase.querySelectorAll("[data-direction]").forEach((button) => {
  button.addEventListener("click", () => showSlide(currentSlide + Number(button.dataset.direction)));
});
showcase.addEventListener("keydown", (event) => {
  const destinations = { ArrowLeft: currentSlide - 1, ArrowRight: currentSlide + 1, Home: 0, End: slides.length - 1 };
  if (!(event.key in destinations)) return;
  event.preventDefault();
  // Focus must remain outside the article that is about to become inert.
  if (event.target.closest(".showcase-slide")) showcase.focus({ preventScroll: true });
  showSlide(destinations[event.key]);
});

let swipeStart = null;
let swiped = false;
const stage = showcase.querySelector(".showcase-slides");
stage.addEventListener("pointerdown", (event) => {
  swiped = false;
  if (event.pointerType === "touch" && event.isPrimary) swipeStart = { x: event.clientX, y: event.clientY };
});
stage.addEventListener("pointercancel", () => { swipeStart = null; });
stage.addEventListener("pointerup", (event) => {
  if (!swipeStart) return;
  const dx = event.clientX - swipeStart.x;
  const dy = event.clientY - swipeStart.y;
  swipeStart = null;
  if (Math.abs(dx) > 50 && Math.abs(dx) > Math.abs(dy) * 1.5) {
    swiped = true;
    showSlide(currentSlide + (dx < 0 ? 1 : -1));
  }
});
stage.addEventListener("click", (event) => {
  if (swiped && event.detail > 0) { event.preventDefault(); swiped = false; }
}, true);

const preview = document.querySelector(".image-preview");
const canHover = window.matchMedia("(hover: hover) and (pointer: fine)");

function closePreview() {
  preview.hidden = true;
  preview.replaceChildren();
}

function showPreview(image) {
  const copy = image.cloneNode(true);
  copy.removeAttribute("class");
  copy.removeAttribute("tabindex");
  copy.removeAttribute("loading");
  copy.removeAttribute("aria-labelledby");
  for (const element of [copy, ...copy.querySelectorAll("[id]")]) element.removeAttribute("id");
  preview.replaceChildren(copy);
  preview.hidden = false;
}

document.querySelectorAll(".product-image:not([data-no-preview])").forEach((image) => {
  image.tabIndex = 0;
  image.addEventListener("mouseenter", () => {
    if (canHover.matches) showPreview(image);
  });
  image.addEventListener("mouseleave", () => {
    if (!image.matches(":focus-visible")) closePreview();
  });
  image.addEventListener("focus", () => {
    if (image.matches(":focus-visible")) showPreview(image);
  });
  image.addEventListener("blur", closePreview);
});
document.addEventListener("keydown", (event) => {
  if (event.key === "Escape") closePreview();
});

const storage = document.querySelector(".storage-figure");
const storageTrack = storage.querySelector(".storage-track");
const storagePacket = storage.querySelector(".storage-packet");
const storageReplay = storage.querySelector(".storage-replay");

new ResizeObserver(() => {
  const vertical = window.matchMedia("(max-width: 680px)").matches;
  storagePacket.style.setProperty("--copy-x", vertical ? "0px" : `${storageTrack.clientWidth - storagePacket.offsetWidth}px`);
  storagePacket.style.setProperty("--copy-y", vertical ? `${storageTrack.clientHeight - storagePacket.offsetHeight}px` : "0px");
}).observe(storageTrack);

function playStorage() {
  if (reducedMotion.matches) return;
  storageReplay.disabled = true;
  storage.classList.add("is-playing");
}

storagePacket.addEventListener("animationend", (event) => {
  if (event.animationName !== "storage-copy") return;
  storage.classList.remove("is-playing");
  storageReplay.disabled = false;
});
storageReplay.addEventListener("click", playStorage);
reducedMotion.addEventListener("change", () => {
  storage.classList.remove("is-playing");
  storageReplay.disabled = reducedMotion.matches;
});

if (!reducedMotion.matches) {
  storageReplay.disabled = false;
  if ("IntersectionObserver" in window) {
    const storageObserver = new IntersectionObserver((entries) => {
      if (entries.some((entry) => entry.isIntersecting)) {
        playStorage();
        storageObserver.disconnect();
      }
    }, { threshold: 0.35 });
    storageObserver.observe(storage);
  } else {
    playStorage();
  }
}
