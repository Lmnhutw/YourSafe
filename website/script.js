"use strict";

const menuButton = document.querySelector(".menu-toggle");
const navigation = document.querySelector(".site-nav");

function closeMenu() {
  navigation.classList.remove("is-open");
  menuButton.setAttribute("aria-expanded", "false");
}

// The unenhanced navigation stays visible when JavaScript is unavailable.
menuButton.hidden = false;
navigation.classList.add("is-collapsible");
menuButton.addEventListener("click", () => {
  const open = navigation.classList.toggle("is-open");
  menuButton.setAttribute("aria-expanded", String(open));
});
navigation.addEventListener("click", (event) => {
  if (event.target.closest("a")) closeMenu();
});
document.addEventListener("keydown", (event) => {
  if (event.key === "Escape" && navigation.classList.contains("is-open")) {
    closeMenu();
    menuButton.focus();
  }
});
const mobile = window.matchMedia("(max-width: 680px)");
function updateMenu() {
  menuButton.hidden = !mobile.matches;
  closeMenu();
}
updateMenu();
mobile.addEventListener("change", updateMenu);

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

const copyButton = document.querySelector(".copy-button");
const copyStatus = document.querySelector(".copy-status");
if (navigator.clipboard && window.isSecureContext) {
  copyButton.hidden = false;
  copyButton.addEventListener("click", async () => {
    try {
      await navigator.clipboard.writeText(
        document.querySelector("#checksum-command").textContent,
      );
      copyStatus.textContent =
        "Đã sao chép. Thay tên file trước khi chạy lệnh.";
    } catch {
      copyStatus.textContent =
        "Không sao chép được. Hãy chọn và sao chép lệnh ở trên.";
    }
  });
}

const showcase = document.querySelector(".showcase");
const slides = [...showcase.querySelectorAll(".showcase-slide")];
const selectors = [...showcase.querySelectorAll("[data-slide]")];
let currentSlide = 0;

function showSlide(index, announce = true) {
  currentSlide = (index + slides.length) % slides.length;
  slides.forEach((slide, position) => {
    slide.hidden = position !== currentSlide;
    slide.inert = position !== currentSlide;
    selectors[position].setAttribute("aria-current", String(position === currentSlide));
  });
  if (announce) {
    showcase.querySelector(".showcase-status").textContent =
      `${currentSlide + 1} / ${slides.length}: ${slides[currentSlide].querySelector("h3").textContent}`;
  }
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
