"use strict";

const form = document.querySelector("#demo-form");
const fields = document.querySelector("#login-fields");
const success = document.querySelector("#login-success");
const status = document.querySelector("#demo-status");

form.addEventListener("submit", (event) => {
  event.preventDefault();
  form.reset();
  fields.hidden = true;
  success.hidden = false;
  status.textContent = "Đăng nhập thử thành công";
  document.querySelector("#success-title").focus();
});

document.querySelector("#try-again").addEventListener("click", () => {
  form.reset();
  success.hidden = true;
  fields.hidden = false;
  status.textContent = "";
  document.querySelector("#demo-username").focus();
});

window.addEventListener("pagehide", () => form.reset());
// Enable only after the submit handler is attached; CSP also blocks form navigation.
form.querySelector("fieldset").disabled = false;

const address = document.querySelector("#demo-address");
const demoUrl = new URL("./demo-login.html", location.href).href;
address.href = demoUrl;
address.textContent = demoUrl;

if (navigator.clipboard && window.isSecureContext) {
  const copyButton = document.querySelector("#copy-address");
  const copyStatus = document.querySelector("#address-status");
  copyButton.hidden = false;
  copyButton.addEventListener("click", async () => {
    try {
      await navigator.clipboard.writeText(demoUrl);
      copyStatus.textContent = "Đã sao chép địa chỉ.";
    } catch {
      copyStatus.textContent = "Không sao chép được. Hãy chọn và sao chép địa chỉ ở trên.";
    }
  });
}
