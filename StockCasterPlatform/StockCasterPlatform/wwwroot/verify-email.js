(() => {
  const message = document.getElementById("verificationMessage");
  const token = new URLSearchParams(location.search).get("token");
  if (!token) {
    message.textContent = "인증 링크가 올바르지 않습니다.";
    return;
  }
  fetch("/api/auth/verify-email", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ token })
  }).then(async response => {
    const data = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(data.error || "이메일 인증에 실패했습니다.");
    message.textContent = data.message || "이메일 인증이 완료되었습니다. 이제 로그인할 수 있습니다.";
  }).catch(error => { message.textContent = error.message; });
})();
