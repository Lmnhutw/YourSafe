"use strict";

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
