"use strict";

// Set before CSS paints so mobile navigation does not briefly expand on load.
document.documentElement.classList.add("has-js");
document.addEventListener("DOMContentLoaded", () => {
  const header = document.querySelector(".site-header");
  new ResizeObserver(() => {
    document.documentElement.style.setProperty("--header-height", `${header.offsetHeight}px`);
  }).observe(header);

  const menuButton = document.querySelector(".menu-toggle");
  const navigation = document.querySelector(".site-nav");
  if (!menuButton || !navigation) return;

  function closeMenu() {
    navigation.classList.remove("is-open");
    menuButton.setAttribute("aria-expanded", "false");
  }

  menuButton.hidden = false;
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
  const mobile = window.matchMedia("(max-width: 960px)");
  function updateMenu() {
    menuButton.hidden = !mobile.matches;
    closeMenu();
  }
  updateMenu();
  mobile.addEventListener("change", updateMenu);
});
