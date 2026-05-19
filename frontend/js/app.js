/* =========================================================================
   Simple Login — Vanilla JS (SL-006)
   - Email + password validation
   - Inline error messages that clear on input
   - Password show/hide toggle
   - Mock success on valid submit (no real auth)
   ========================================================================= */

(function () {
  "use strict";

  // ---- Constants ---------------------------------------------------------
  // Pragmatic email regex — RFC-5322 in full is overkill for client-side UX.
  // This catches the vast majority of typos while staying readable.
  var EMAIL_RE = /^[^\s@]+@[^\s@]+\.[^\s@]{2,}$/;
  var PASSWORD_MIN_LENGTH = 8;

  // ---- DOM lookup --------------------------------------------------------
  var form = document.getElementById("loginForm");
  var emailInput = document.getElementById("email");
  var passwordInput = document.getElementById("password");
  var emailError = document.getElementById("emailError");
  var passwordError = document.getElementById("passwordError");
  var togglePasswordBtn = document.getElementById("togglePassword");
  var formStatus = document.getElementById("formStatus");
  var copyrightYear = document.getElementById("copyrightYear");
  var socialButtons = document.querySelectorAll("[data-provider]");
  var themeToggleBtn = document.getElementById("themeToggle");

  // ---- Theme toggle (SL-011) -----------------------------------------------
  function getStoredTheme() {
    return localStorage.getItem("color-scheme");
  }

  function getSystemTheme() {
    return window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
  }

  function applyTheme(theme) {
    if (theme === "dark") {
      document.documentElement.setAttribute("data-theme", "dark");
    } else {
      document.documentElement.removeAttribute("data-theme");
    }
    if (themeToggleBtn) {
      var isDark = theme === "dark";
      var icon = themeToggleBtn.querySelector("i");
      if (icon) {
        icon.classList.toggle("bi-moon", !isDark);
        icon.classList.toggle("bi-sun", isDark);
      }
      themeToggleBtn.setAttribute(
        "aria-label",
        isDark ? "Switch to light mode" : "Switch to dark mode"
      );
    }
  }

  function initTheme() {
    var stored = getStoredTheme();
    applyTheme(stored || getSystemTheme());
  }

  function toggleTheme() {
    var current = document.documentElement.getAttribute("data-theme") === "dark"
      ? "dark" : "light";
    var next = current === "dark" ? "light" : "dark";
    localStorage.setItem("color-scheme", next);
    applyTheme(next);
  }

  initTheme();

  if (themeToggleBtn) {
    themeToggleBtn.addEventListener("click", toggleTheme);
  }

  // Defensive: if the form root isn't on this page, do nothing.
  if (!form) return;

  // ---- Helpers -----------------------------------------------------------
  function setFieldError(input, errorEl, message) {
    if (message) {
      input.classList.add("is-invalid");
      input.setAttribute("aria-invalid", "true");
      errorEl.textContent = message;
    } else {
      input.classList.remove("is-invalid");
      input.removeAttribute("aria-invalid");
      errorEl.textContent = "";
    }
  }

  function showStatus(message, type) {
    if (!formStatus) return;
    formStatus.textContent = message;
    formStatus.classList.remove("is-success", "is-error");
    formStatus.classList.add(type === "error" ? "is-error" : "is-success");
    formStatus.hidden = false;
  }

  function clearStatus() {
    if (!formStatus) return;
    formStatus.hidden = true;
    formStatus.textContent = "";
    formStatus.classList.remove("is-success", "is-error");
  }

  // ---- Validators --------------------------------------------------------
  function validateEmail(value) {
    var trimmed = (value || "").trim();
    if (!trimmed) return "Email is required.";
    if (!EMAIL_RE.test(trimmed)) return "Enter a valid email address.";
    return "";
  }

  function validatePassword(value) {
    if (!value) return "Password is required.";
    if (value.length < PASSWORD_MIN_LENGTH) {
      return "Password must be at least " + PASSWORD_MIN_LENGTH + " characters.";
    }
    return "";
  }

  function validateForm() {
    var emailMsg = validateEmail(emailInput.value);
    var passwordMsg = validatePassword(passwordInput.value);
    setFieldError(emailInput, emailError, emailMsg);
    setFieldError(passwordInput, passwordError, passwordMsg);
    return !emailMsg && !passwordMsg;
  }

  // ---- Event wiring ------------------------------------------------------
  form.addEventListener("submit", function (event) {
    event.preventDefault();
    clearStatus();

    var isValid = validateForm();
    if (!isValid) {
      // Move focus to the first invalid field for accessibility.
      var firstInvalid = form.querySelector(".is-invalid");
      if (firstInvalid) firstInvalid.focus();
      return;
    }

    // Mock success — no network call, no credential logging.
    showStatus(
      "Signed in successfully (demo). This page does not perform real authentication.",
      "success"
    );

    // Optional: clear password field after mock success
    passwordInput.value = "";
  });

  // Clear individual field errors as the user corrects them.
  emailInput.addEventListener("input", function () {
    if (emailInput.classList.contains("is-invalid")) {
      var msg = validateEmail(emailInput.value);
      if (!msg) setFieldError(emailInput, emailError, "");
    }
  });

  passwordInput.addEventListener("input", function () {
    if (passwordInput.classList.contains("is-invalid")) {
      var msg = validatePassword(passwordInput.value);
      if (!msg) setFieldError(passwordInput, passwordError, "");
    }
  });

  // Password show/hide toggle
  if (togglePasswordBtn) {
    togglePasswordBtn.addEventListener("click", function () {
      var isHidden = passwordInput.getAttribute("type") === "password";
      passwordInput.setAttribute("type", isHidden ? "text" : "password");
      togglePasswordBtn.setAttribute("aria-pressed", String(isHidden));
      togglePasswordBtn.setAttribute(
        "aria-label",
        isHidden ? "Hide password" : "Show password"
      );

      var icon = togglePasswordBtn.querySelector("i");
      if (icon) {
        icon.classList.toggle("bi-eye", !isHidden);
        icon.classList.toggle("bi-eye-slash", isHidden);
      }

      // Keep focus on the toggle so keyboard users don't lose context.
      togglePasswordBtn.focus();
    });
  }

  // Social buttons — visual only. Show a friendly notice; never submit.
  socialButtons.forEach(function (btn) {
    btn.addEventListener("click", function () {
      var provider = btn.getAttribute("data-provider") || "the provider";
      var label = provider.charAt(0).toUpperCase() + provider.slice(1);
      showStatus(
        label + " sign-in is not wired up in this demo.",
        "success"
      );
    });
  });

  // Footer copyright year
  if (copyrightYear) {
    copyrightYear.textContent = String(new Date().getFullYear());
  }
})();
