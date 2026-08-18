(() => {
  const requestPanel = document.getElementById("requestPanel");
  const resetPanel = document.getElementById("resetPanel");
  const message = document.getElementById("resetMessage");
  const token = new URLSearchParams(location.search).get("token");

  const request = async (url, options = {}) => {
    const response = await fetch(url, { ...options, headers: { "Content-Type": "application/json" } });
    const data = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(data.error || "요청을 처리하지 못했습니다.");
    return data;
  };

  const showMessage = (text, isError = false) => {
    message.textContent = text;
    message.classList.toggle("error", isError);
    message.classList.remove("hidden");
  };

  if (token) {
    requestPanel.classList.add("hidden");
    resetPanel.classList.remove("hidden");
  }

  document.getElementById("requestForm")?.addEventListener("submit", async event => {
    event.preventDefault();
    try {
      const result = await request("/api/auth/forgot-password", {
        method: "POST",
        body: JSON.stringify({ email: document.getElementById("resetEmail").value })
      });
      event.currentTarget.reset();
      showMessage(result.message);
    } catch (error) { showMessage(error.message, true); }
  });

  document.getElementById("resetForm")?.addEventListener("submit", async event => {
    event.preventDefault();
    const password = document.getElementById("newResetPassword").value;
    if (password !== document.getElementById("confirmResetPassword").value) {
      showMessage("비밀번호 확인이 일치하지 않습니다.", true);
      return;
    }
    try {
      const result = await request("/api/auth/reset-password", {
        method: "POST",
        body: JSON.stringify({ token, newPassword: password })
      });
      event.currentTarget.reset();
      showMessage(`${result.message} 잠시 후 로그인 화면으로 이동합니다.`);
      setTimeout(() => location.assign("/"), 1500);
    } catch (error) { showMessage(error.message, true); }
  });
})();
