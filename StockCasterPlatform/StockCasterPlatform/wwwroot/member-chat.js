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
    chatLoggedOut: document.getElementById("chatLoggedOut"),
    chatLive: document.getElementById("chatLive"),
    chatMessages: document.getElementById("chatMessages"),
    chatNotice: document.getElementById("chatNotice"),
    chatForm: document.getElementById("chatForm"),
    chatInput: document.getElementById("chatInput"),
    chatSendButton: document.getElementById("chatSendButton"),
    connectionBadge: document.getElementById("chatConnectionBadge"),
    pinnedNotice: document.getElementById("chatPinnedNotice"),
    pinnedNoticeText: document.getElementById("chatPinnedNoticeText"),
    adminLink: document.getElementById("adminLink"),
    accountTier: document.getElementById("accountTier")
  };

  let currentUser = null;
  let socket = null;
  let reconnectTimer = null;
  let reconnectAttempt = 0;
  let cooldownTimer = null;
  let restrictionTimer = null;
  let chatPolicy = { pinnedNotice: "", slowModeSeconds: 0 };

  const request = async (url, options = {}) => {
    const response = await fetch(url, {
      ...options,
      headers: { "Content-Type": "application/json", ...(options.headers || {}) }
    });
    const data = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(data.error || "요청을 처리하지 못했습니다.");
    return data;
  };

  const showModal = () => {
    elements.modal.classList.remove("hidden");
    elements.modal.setAttribute("aria-hidden", "false");
    document.body.classList.add("modal-open");
    renderAccountState();
    setTimeout(() => (currentUser ? elements.logoutButton : document.getElementById("loginUsername"))?.focus(), 20);
  };

  const hideModal = () => {
    elements.modal.classList.add("hidden");
    elements.modal.setAttribute("aria-hidden", "true");
    document.body.classList.remove("modal-open");
    setError("");
  };

  const setError = message => {
    elements.authError.textContent = message;
    elements.authError.classList.toggle("hidden", !message);
  };

  const selectTab = tab => {
    document.querySelectorAll("[data-auth-tab]").forEach(button => button.classList.toggle("active", button.dataset.authTab === tab));
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
    elements.adminLink.classList.toggle("hidden", !["Admin", "ChatModerator"].includes(currentUser.role));
    elements.resendVerificationButton.classList.toggle("hidden", Boolean(currentUser.emailVerified));
  };

  const renderAuth = () => {
    elements.memberButton.textContent = currentUser ? `${currentUser.displayName} 님` : "로그인 · 회원가입";
    elements.memberButton.classList.toggle("signed-in", Boolean(currentUser));
    elements.chatLoggedOut.classList.toggle("hidden", Boolean(currentUser));
    elements.chatLive.classList.toggle("hidden", !currentUser);
    elements.chatForm.classList.toggle("hidden", !currentUser);
    if (currentUser) {
      applyRestriction(Boolean(currentUser.isMuted), currentUser.mutedUntil);
      connectChat();
    } else {
      disconnectChat();
      clearTimeout(restrictionTimer);
      clearInterval(cooldownTimer);
      elements.chatMessages.replaceChildren();
      setConnectionBadge("로그인 필요", "");
    }
    window.dispatchEvent(new CustomEvent("stockcaster:auth-changed", { detail: { user: currentUser } }));
  };

  const setConnectionBadge = (text, state) => {
    elements.connectionBadge.textContent = text;
    elements.connectionBadge.className = `coming-badge ${state}`.trim();
  };

  const showChatNotice = (message, isError = false) => {
    elements.chatNotice.textContent = message;
    elements.chatNotice.classList.toggle("error", isError);
    elements.chatNotice.classList.remove("hidden");
    clearTimeout(showChatNotice.timer);
    showChatNotice.timer = setTimeout(() => elements.chatNotice.classList.add("hidden"), 4000);
  };

  const renderPolicy = policy => {
    chatPolicy = {
      pinnedNotice: policy?.pinnedNotice || "",
      slowModeSeconds: Number(policy?.slowModeSeconds) || 0
    };
    elements.pinnedNoticeText.textContent = chatPolicy.pinnedNotice;
    elements.pinnedNotice.classList.toggle("hidden", !chatPolicy.pinnedNotice);
    if (currentUser && !currentUser.isMuted)
      elements.chatInput.placeholder = chatPolicy.slowModeSeconds
        ? `${chatPolicy.slowModeSeconds}초 슬로우 모드 · 메시지를 입력하세요`
        : "메시지를 입력하세요";
  };

  const formatRestrictionTime = value => new Intl.DateTimeFormat("ko-KR", {
    month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit"
  }).format(new Date(value));

  const applyRestriction = (muted, mutedUntil = null) => {
    if (!currentUser) return;
    clearTimeout(restrictionTimer);
    clearInterval(cooldownTimer);

    if (muted && mutedUntil && new Date(mutedUntil).getTime() <= Date.now()) {
      muted = false;
      mutedUntil = null;
    }

    currentUser.isMuted = muted;
    currentUser.mutedUntil = mutedUntil || null;
    elements.chatInput.disabled = muted;
    elements.chatSendButton.disabled = muted;
    elements.chatSendButton.textContent = "전송";
    if (!muted) {
      elements.chatInput.placeholder = chatPolicy.slowModeSeconds
        ? `${chatPolicy.slowModeSeconds}초 슬로우 모드 · 메시지를 입력하세요`
        : "메시지를 입력하세요";
      return;
    }

    elements.chatInput.placeholder = mutedUntil
      ? `${formatRestrictionTime(mutedUntil)}까지 채팅 제한`
      : "관리자에 의해 채팅이 영구 제한되었습니다";
    if (mutedUntil) {
      const delay = Math.max(0, new Date(mutedUntil).getTime() - Date.now());
      restrictionTimer = setTimeout(() => {
        applyRestriction(false, null);
        showChatNotice("채팅 제한 시간이 종료되었습니다.");
      }, Math.min(delay + 200, 2147483647));
    }
  };

  const beginCooldown = seconds => {
    if (!currentUser || currentUser.isMuted || !seconds) return;
    clearInterval(cooldownTimer);
    const endAt = Date.now() + Math.max(1, seconds) * 1000;
    const update = () => {
      const remaining = Math.ceil((endAt - Date.now()) / 1000);
      if (remaining <= 0) {
        clearInterval(cooldownTimer);
        cooldownTimer = null;
        if (!currentUser?.isMuted) elements.chatSendButton.disabled = false;
        elements.chatSendButton.textContent = "전송";
        return;
      }
      elements.chatSendButton.disabled = true;
      elements.chatSendButton.textContent = `${remaining}초`;
    };
    update();
    cooldownTimer = setInterval(update, 250);
  };

  const formatTime = value => new Intl.DateTimeFormat("ko-KR", {
    hour: "2-digit", minute: "2-digit", hour12: false
  }).format(new Date(value));

  const renderMessage = message => {
    if (document.querySelector(`[data-message-id="${message.id}"]`)) return;
    const row = document.createElement("article");
    row.className = `chat-message${message.memberId === currentUser?.id ? " mine" : ""}${message.isAdmin ? " admin" : ""}`;
    row.dataset.messageId = message.id;

    const meta = document.createElement("div");
    meta.className = "chat-message-meta";
    const name = document.createElement("strong");
    name.textContent = message.displayName;
    const time = document.createElement("time");
    time.dateTime = message.sentAt;
    time.textContent = formatTime(message.sentAt);
    meta.append(name, time);

    const text = document.createElement("p");
    text.textContent = message.text;
    row.append(meta, text);
    if (message.memberId !== currentUser?.id && !message.isAdmin) {
      const reportButton = document.createElement("button");
      reportButton.type = "button";
      reportButton.className = "chat-report-button";
      reportButton.textContent = "신고";
      reportButton.addEventListener("click", async () => {
        const details = prompt("신고 사유를 간단히 입력해 주세요.", "부적절한 채팅");
        if (details === null) return;
        try {
          await request("/api/reports", {
            method: "POST",
            body: JSON.stringify({ messageId: message.id, category: "채팅", details })
          });
          showChatNotice("신고가 접수되었습니다.");
        } catch (error) { showChatNotice(error.message, true); }
      });
      row.append(reportButton);
    }
    elements.chatMessages.append(row);
    elements.chatMessages.scrollTop = elements.chatMessages.scrollHeight;
  };

  const handleSocketMessage = event => {
    const payload = JSON.parse(event.data);
    if (payload.type === "connected") {
      reconnectAttempt = 0;
      currentUser = { ...currentUser, ...payload.user };
      renderPolicy(payload.policy);
      applyRestriction(Boolean(currentUser.isMuted), currentUser.mutedUntil);
      elements.chatMessages.replaceChildren();
      (payload.messages || []).forEach(renderMessage);
      setConnectionBadge(`${payload.onlineCount || 1}명 참여`, "connected");
    } else if (payload.type === "message") {
      renderMessage(payload.message);
    } else if (payload.type === "deleted") {
      document.querySelector(`[data-message-id="${payload.messageId}"]`)?.remove();
    } else if (payload.type === "presence") {
      setConnectionBadge(`${payload.onlineCount || 0}명 참여`, "connected");
    } else if (payload.type === "policy") {
      renderPolicy(payload.policy);
      showChatNotice("채팅 운영정책이 변경되었습니다.");
    } else if (payload.type === "muted") {
      applyRestriction(true, payload.mutedUntil);
      showChatNotice(payload.message, true);
    } else if (payload.type === "memberState" && payload.memberId === currentUser?.id) {
      applyRestriction(payload.muted, payload.mutedUntil);
      showChatNotice(payload.muted ? "채팅이 제한되었습니다." : "채팅 제한이 해제되었습니다.", payload.muted);
    } else if (payload.type === "error") {
      if (payload.retryAfterSeconds) beginCooldown(payload.retryAfterSeconds);
      showChatNotice(payload.message, true);
    }
  };

  const connectChat = () => {
    if (!currentUser || socket?.readyState === WebSocket.OPEN || socket?.readyState === WebSocket.CONNECTING) return;
    clearTimeout(reconnectTimer);
    const protocol = location.protocol === "https:" ? "wss" : "ws";
    socket = new WebSocket(`${protocol}://${location.host}/ws/chat`);
    setConnectionBadge("연결 중", "connecting");
    socket.addEventListener("message", handleSocketMessage);
    socket.addEventListener("close", () => {
      socket = null;
      if (!currentUser) return;
      setConnectionBadge("재연결 중", "connecting");
      reconnectAttempt += 1;
      reconnectTimer = setTimeout(connectChat, Math.min(1000 * 2 ** reconnectAttempt, 10000));
    });
    socket.addEventListener("error", () => socket?.close());
  };

  const disconnectChat = () => {
    clearTimeout(reconnectTimer);
    if (socket) {
      socket.onclose = null;
      socket.close();
      socket = null;
    }
  };

  elements.memberButton.addEventListener("click", showModal);
  document.getElementById("chatLoginButton").addEventListener("click", showModal);
  elements.closeButton.addEventListener("click", hideModal);
  elements.modal.addEventListener("click", event => { if (event.target === elements.modal) hideModal(); });
  document.addEventListener("keydown", event => { if (event.key === "Escape") hideModal(); });
  document.querySelectorAll("[data-auth-tab]").forEach(button => button.addEventListener("click", () => selectTab(button.dataset.authTab)));

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
      renderAccountState();
      renderAuth();
      hideModal();
    } catch (error) { setError(error.message); }
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
      renderAccountState();
      renderAuth();
      hideModal();
    } catch (error) { setError(error.message); }
  });

  elements.logoutButton.addEventListener("click", async () => {
    await request("/api/auth/logout", { method: "POST", body: "{}" });
    currentUser = null;
    hideModal();
    renderAuth();
  });

  elements.resendVerificationButton.addEventListener("click", async () => {
    try {
      const result = await request("/api/auth/resend-verification", { method: "POST", body: "{}" });
      showChatNotice(result.message || "인증 이메일을 보냈습니다.");
    } catch (error) { showChatNotice(error.message, true); }
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
    } catch (error) { showChatNotice(error.message, true); }
  });

  elements.chatForm.addEventListener("submit", event => {
    event.preventDefault();
    const text = elements.chatInput.value.trim();
    if (!text || elements.chatSendButton.disabled || socket?.readyState !== WebSocket.OPEN) return;
    socket.send(JSON.stringify({ type: "send", text }));
    elements.chatInput.value = "";
    beginCooldown(chatPolicy.slowModeSeconds);
  });

  window.addEventListener("load", async () => {
    try {
      const [authResult, policy] = await Promise.all([
        request("/api/auth/me"),
        request("/api/chat/policy")
      ]);
      currentUser = authResult.isAuthenticated ? authResult.user : null;
      renderPolicy(policy);
    } catch {
      currentUser = null;
    }
    renderAuth();
  });
})();
