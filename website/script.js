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
