(() => {
  const elements = {
    memberButton: document.getElementById("memberButton"),
    modal: document.getElementById("authModal"),
    closeButton: document.getElementById("authCloseButton"),
    authForms: document.getElementById("authForms"),
    accountPanel: document.getElementById("accountPanel"),
    authError: document.getElementById("authError"),
    loginForm: document.getElementById("loginForm"),
    registerForm: document.getElementById("registerForm"),
    logoutButton: document.getElementById("logoutButton"),
    resendVerificationButton: document.getElementById("resendVerificationButton"),
    withdrawButton: document.getElementById("withdrawButton"),
    accountTier: document.getElementById("accountTier"),
    adminLink: document.getElementById("adminLink"),
    studioLink: document.getElementById("studioLink"),
    heroJoinButton: document.getElementById("heroJoinButton"),
    journeyJoinButton: document.getElementById("journeyJoinButton"),
    membershipJoinButton: document.getElementById("membershipJoinButton"),
    bottomJoinButton: document.getElementById("bottomJoinButton"),
    heroLiveBadge: document.getElementById("heroLiveBadge"),
    heroViewerCount: document.getElementById("heroViewerCount"),
    heroBroadcastTitle: document.getElementById("heroBroadcastTitle"),
    statusOrb: document.getElementById("landingStatusOrb"),
    statusText: document.getElementById("landingStatusText"),
    schedule: document.getElementById("landingSchedule"),
    broadcastTitle: document.getElementById("landingBroadcastTitle"),
    broadcastDescription: document.getElementById("landingBroadcastDescription"),
    broadcastButton: document.getElementById("landingBroadcastButton")
  };

  let currentUser = null;
  let broadcastInfo = null;

  const request = async (url, options = {}) => {
    const response = await fetch(url, {
      ...options,
      headers: { "Content-Type": "application/json", ...(options.headers || {}) }
    });
    const data = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(data.error || "요청을 처리하지 못했습니다.");
    return data;
  };

  const setError = message => {
    elements.authError.textContent = message;
    elements.authError.classList.toggle("hidden", !message);
  };

  const selectTab = tab => {
    document.querySelectorAll("[data-auth-tab]").forEach(button =>
      button.classList.toggle("active", button.dataset.authTab === tab));
    elements.loginForm.classList.toggle("hidden", tab !== "login");
    elements.registerForm.classList.toggle("hidden", tab !== "register");
    document.getElementById("authTitle").textContent = tab === "login" ? "회원 로그인" : "새 회원가입";
    setError("");
  };

  const renderAccountState = () => {
    elements.authForms.classList.toggle("hidden", Boolean(currentUser));
    elements.accountPanel.classList.toggle("hidden", !currentUser);
    if (!currentUser) return;

    document.getElementById("accountName").textContent = currentUser.displayName;
    document.getElementById("accountUsername").textContent = `@${currentUser.username}`;
    document.getElementById("accountAvatar").textContent = currentUser.displayName.slice(0, 2).toUpperCase();
    elements.accountTier.textContent = currentUser.roleLabel
      ? `${currentUser.roleLabel} · ${currentUser.emailVerified ? "이메일 인증 완료" : "이메일 인증 필요"}`
      : currentUser.canWatchReplay
        ? "프리미엄 회원 · 라이브·다시보기"
        : "무료 회원 · 라이브 시청";
    elements.accountTier.classList.toggle("premium", Boolean(currentUser.canWatchReplay));
    elements.adminLink.classList.toggle("hidden", !currentUser.isAdmin);
    elements.studioLink.classList.toggle("hidden", !["Admin", "Broadcaster"].includes(currentUser.role));
    elements.resendVerificationButton.classList.toggle("hidden", Boolean(currentUser.emailVerified));
  };

  const renderAuth = () => {
    elements.memberButton.textContent = currentUser ? `${currentUser.displayName} 님` : "로그인 · 회원가입";
    elements.memberButton.classList.toggle("signed-in", Boolean(currentUser));
    elements.heroJoinButton.textContent = currentUser ? "회원 방송으로 이동" : "무료 회원가입";
    elements.journeyJoinButton.firstChild.textContent = currentUser ? "회원 방송으로 이동 " : "무료로 시작하기 ";
    elements.membershipJoinButton.textContent = currentUser ? "내 회원정보 보기" : "회원가입 안내";
    elements.bottomJoinButton.textContent = currentUser ? "회원 방송 입장" : "무료 회원가입";
    renderAccountState();
  };

  const showModal = (tab = "login") => {
    if (!currentUser) selectTab(tab);
    renderAccountState();
    elements.modal.classList.remove("hidden");
    elements.modal.setAttribute("aria-hidden", "false");
    document.body.classList.add("modal-open");
    setTimeout(() => {
      const target = currentUser
        ? elements.logoutButton
        : document.getElementById(tab === "register" ? "registerUsername" : "loginUsername");
      target?.focus();
    }, 20);
  };

  const hideModal = () => {
    elements.modal.classList.add("hidden");
    elements.modal.setAttribute("aria-hidden", "true");
    document.body.classList.remove("modal-open");
    setError("");
  };

  const handleJoinAction = () => {
    if (currentUser) {
      window.location.assign("/live");
      return;
    }
    showModal("register");
  };

  const formatSchedule = value => {
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return null;
    return new Intl.DateTimeFormat("ko-KR", {
      month: "long",
      day: "numeric",
      weekday: "short",
      hour: "2-digit",
      minute: "2-digit"
    }).format(date);
  };

  const renderBroadcastState = status => {
    const isLive = Boolean(status?.isLive);
    const title = broadcastInfo?.title || "실시간 증권 차트 방송";
    const description = broadcastInfo?.description || "실시간 차트 분석과 시장 흐름을 전달합니다.";
    const schedule = broadcastInfo?.scheduledAt ? formatSchedule(broadcastInfo.scheduledAt) : null;

    elements.heroLiveBadge.classList.toggle("live", isLive);
    elements.heroLiveBadge.querySelector("b").textContent = isLive ? "LIVE 방송 중" : "방송 대기";
    elements.heroViewerCount.textContent = status?.viewerCount ?? 0;
    elements.heroBroadcastTitle.textContent = title;
    elements.statusOrb.classList.toggle("live", isLive);
    elements.statusText.textContent = isLive ? "지금 라이브 방송이 진행 중입니다" : "다음 방송을 준비하고 있습니다";
    elements.schedule.textContent = isLive ? "지금 LIVE" : schedule || "다음 방송 일정 준비 중";
    elements.broadcastTitle.textContent = title;
    elements.broadcastDescription.textContent = description;
    elements.broadcastButton.firstChild.textContent = isLive ? "LIVE 바로 시청 " : "방송 화면 입장 ";
  };

  const loadBroadcastState = async () => {
    const [statusResult, infoResult] = await Promise.allSettled([
      request("/api/live/status"),
      request("/api/live/info")
    ]);
    if (infoResult.status === "fulfilled") broadcastInfo = infoResult.value;
    renderBroadcastState(statusResult.status === "fulfilled" ? statusResult.value : null);
  };

  elements.memberButton.addEventListener("click", () => showModal("login"));
  elements.closeButton.addEventListener("click", hideModal);
  elements.modal.addEventListener("click", event => { if (event.target === elements.modal) hideModal(); });
  document.addEventListener("keydown", event => { if (event.key === "Escape") hideModal(); });
  document.querySelectorAll("[data-auth-tab]").forEach(button =>
    button.addEventListener("click", () => selectTab(button.dataset.authTab)));
  document.querySelectorAll("[data-open-login]").forEach(button =>
    button.addEventListener("click", () => showModal("login")));
  document.querySelectorAll("[data-open-register]").forEach(button =>
    button.addEventListener("click", handleJoinAction));
  [elements.heroJoinButton, elements.journeyJoinButton, elements.membershipJoinButton, elements.bottomJoinButton]
    .forEach(button => button.addEventListener("click", handleJoinAction));

  elements.loginForm.addEventListener("submit", async event => {
    event.preventDefault();
    setError("");
    try {
      const result = await request("/api/auth/login", {
        method: "POST",
        body: JSON.stringify({
          username: document.getElementById("loginUsername").value,
          password: document.getElementById("loginPassword").value
        })
      });
      currentUser = result.user;
      elements.loginForm.reset();
      renderAuth();
      window.location.assign("/live");
    } catch (error) {
      setError(error.message);
    }
  });

  elements.registerForm.addEventListener("submit", async event => {
    event.preventDefault();
    setError("");
    try {
      const result = await request("/api/auth/register", {
        method: "POST",
        body: JSON.stringify({
          username: document.getElementById("registerUsername").value,
          displayName: document.getElementById("registerDisplayName").value,
          email: document.getElementById("registerEmail").value,
          password: document.getElementById("registerPassword").value,
          termsAccepted: document.getElementById("registerTerms").checked,
          privacyAccepted: document.getElementById("registerPrivacy").checked
        })
      });
      if (result.emailVerificationRequired) {
        elements.registerForm.reset();
        setError(result.message || "이메일 인증 후 로그인해 주세요.");
        selectTab("login");
        return;
      }
      currentUser = result.user;
      elements.registerForm.reset();
      renderAuth();
      window.location.assign("/live");
    } catch (error) {
      setError(error.message);
    }
  });

  elements.logoutButton.addEventListener("click", async () => {
    try {
      await request("/api/auth/logout", { method: "POST", body: "{}" });
    } finally {
      currentUser = null;
      hideModal();
      renderAuth();
    }
  });

  elements.resendVerificationButton.addEventListener("click", async () => {
    try {
      const result = await request("/api/auth/resend-verification", { method: "POST", body: "{}" });
      setError(result.message || "인증 이메일을 보냈습니다.");
    } catch (error) { setError(error.message); }
  });

  elements.withdrawButton.addEventListener("click", async () => {
    const password = prompt("탈퇴를 확인하려면 현재 비밀번호를 입력해 주세요.");
    if (password === null) return;
    if (!confirm("회원 탈퇴 후 계정과 채팅 기록은 복구할 수 없습니다. 계속할까요?")) return;
    try {
      await request("/api/auth/withdraw", { method: "POST", body: JSON.stringify({ password }) });
      currentUser = null;
      hideModal();
      renderAuth();
      alert("회원 탈퇴가 완료되었습니다.");
    } catch (error) { setError(error.message); }
  });

  window.addEventListener("load", async () => {
    try {
      const result = await request("/api/auth/me");
      currentUser = result.isAuthenticated ? result.user : null;
    } catch {
      currentUser = null;
    }
    renderAuth();
    await loadBroadcastState();
    setInterval(loadBroadcastState, 10000);
  });
})();
