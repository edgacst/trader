(() => {
  const elements = {
    grid: document.getElementById("replayGrid"),
    empty: document.getElementById("replayEmpty"),
    count: document.getElementById("replayCountBadge"),
    memberButton: document.getElementById("replayMemberButton"),
    authModal: document.getElementById("replayAuthModal"),
    loginPanel: document.getElementById("replayLoginPanel"),
    accountPanel: document.getElementById("replayAccountPanel"),
    authError: document.getElementById("replayAuthError"),
    accountTier: document.getElementById("replayAccountTier"),
    accessNotice: document.getElementById("replayAccessNotice"),
    playerModal: document.getElementById("replayPlayerModal"),
    player: document.getElementById("replayVideo"),
    playerError: document.getElementById("replayPlayerError")
  };
  let currentUser = null;
  let pendingReplay = null;

  const request = async (url, options = {}) => {
    const response = await fetch(url, { cache: "no-store", ...options, headers: { "Content-Type": "application/json", ...(options.headers || {}) } });
    const data = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(data.error || "요청을 처리하지 못했습니다.");
    return data;
  };

  const formatDuration = seconds => {
    const total = Math.max(0, Math.round(seconds));
    const hours = Math.floor(total / 3600);
    const minutes = Math.floor((total % 3600) / 60);
    const secs = total % 60;
    return hours ? `${hours}:${String(minutes).padStart(2, "0")}:${String(secs).padStart(2, "0")}` : `${minutes}:${String(secs).padStart(2, "0")}`;
  };

  const formatDate = value => new Intl.DateTimeFormat("ko-KR", {
    year: "numeric", month: "long", day: "numeric", hour: "2-digit", minute: "2-digit", hour12: false
  }).format(new Date(value));

  const openAuth = () => {
    elements.loginPanel.classList.toggle("hidden", Boolean(currentUser));
    elements.accountPanel.classList.toggle("hidden", !currentUser);
    if (currentUser) {
      document.getElementById("replayAccountName").textContent = currentUser.displayName;
      document.getElementById("replayAccountUsername").textContent = `@${currentUser.username}`;
      document.getElementById("replayAccountAvatar").textContent = currentUser.displayName.slice(0, 2).toUpperCase();
      elements.accountTier.textContent = currentUser.isAdmin
        ? "운영자 · 전체 시청 권한"
        : currentUser.canWatchReplay
          ? "프리미엄 회원 · 다시보기 가능"
          : "무료 회원 · 라이브 시청 가능";
      elements.accountTier.classList.toggle("premium", Boolean(currentUser.canWatchReplay));
      elements.accessNotice.classList.toggle("hidden", Boolean(currentUser.canWatchReplay));
    }
    elements.authModal.classList.remove("hidden");
    document.body.classList.add("modal-open");
  };

  const closeAuth = () => {
    elements.authModal.classList.add("hidden");
    elements.authError.classList.add("hidden");
    document.body.classList.remove("modal-open");
  };

  const renderMember = () => {
    elements.memberButton.textContent = currentUser ? `${currentUser.displayName} 님` : "로그인 · 회원가입";
    elements.memberButton.classList.toggle("signed-in", Boolean(currentUser));
  };

  const playReplay = replay => {
    if (!currentUser) {
      pendingReplay = replay;
      openAuth();
      return;
    }
    if (!currentUser.canWatchReplay) {
      pendingReplay = null;
      openAuth();
      return;
    }
    document.getElementById("replayPlayerTitle").textContent = replay.title;
    document.getElementById("replayPlayerMeta").textContent = `${formatDate(replay.startedAt)} · ${formatDuration(replay.durationSeconds)}`;
    elements.playerError.classList.add("hidden");
    elements.player.src = replay.videoUrl;
    elements.playerModal.classList.remove("hidden");
    elements.playerModal.setAttribute("aria-hidden", "false");
    document.body.classList.add("modal-open");
    elements.player.play().catch(() => {});
  };

  const closePlayer = () => {
    elements.player.pause();
    elements.player.removeAttribute("src");
    elements.player.load();
    elements.playerModal.classList.add("hidden");
    elements.playerModal.setAttribute("aria-hidden", "true");
    document.body.classList.remove("modal-open");
  };

  const renderReplays = replays => {
    elements.grid.replaceChildren();
    elements.count.textContent = `영상 ${replays.length}개`;
    elements.empty.classList.toggle("hidden", replays.length > 0);
    elements.grid.classList.toggle("hidden", replays.length === 0);

    replays.forEach(replay => {
      const card = document.createElement("button");
      card.type = "button";
      card.className = "replay-card";
      const thumbnail = document.createElement("div");
      thumbnail.className = "replay-thumbnail";
      const image = document.createElement("img");
      image.src = replay.thumbnailUrl;
      image.alt = "";
      const play = document.createElement("span");
      play.className = "replay-play-icon";
      play.textContent = "▶";
      const duration = document.createElement("time");
      duration.textContent = formatDuration(replay.durationSeconds);
      thumbnail.append(image, play, duration);

      const copy = document.createElement("div");
      copy.className = "replay-copy";
      const title = document.createElement("strong");
      title.textContent = replay.title;
      const date = document.createElement("span");
      date.textContent = formatDate(replay.startedAt);
      copy.append(title, date);
      card.append(thumbnail, copy);
      card.addEventListener("click", () => playReplay(replay));
      elements.grid.append(card);
    });
  };

  elements.memberButton.addEventListener("click", openAuth);
  document.getElementById("replayAuthClose").addEventListener("click", closeAuth);
  document.getElementById("replayPlayerClose").addEventListener("click", closePlayer);
  elements.authModal.addEventListener("click", event => { if (event.target === elements.authModal) closeAuth(); });
  elements.playerModal.addEventListener("click", event => { if (event.target === elements.playerModal) closePlayer(); });
  document.addEventListener("keydown", event => { if (event.key === "Escape") { closeAuth(); closePlayer(); } });

  document.getElementById("replayLoginForm").addEventListener("submit", async event => {
    event.preventDefault();
    elements.authError.classList.add("hidden");
    try {
      const result = await request("/api/auth/login", {
        method: "POST",
        body: JSON.stringify({ username: document.getElementById("replayUsername").value, password: document.getElementById("replayPassword").value })
      });
      currentUser = result.user;
      renderMember();
      if (pendingReplay && currentUser.canWatchReplay) {
        const replay = pendingReplay;
        pendingReplay = null;
        closeAuth();
        playReplay(replay);
      } else if (currentUser.canWatchReplay) {
        closeAuth();
      } else {
        pendingReplay = null;
        openAuth();
      }
    } catch (error) {
      elements.authError.textContent = error.message;
      elements.authError.classList.remove("hidden");
    }
  });

  document.getElementById("replayLogoutButton").addEventListener("click", async () => {
    await request("/api/auth/logout", { method: "POST", body: "{}" });
    currentUser = null;
    renderMember();
    closeAuth();
  });

  elements.player.addEventListener("error", () => {
    elements.playerError.textContent = "영상을 불러오지 못했습니다. 잠시 후 다시 시도해 주세요.";
    elements.playerError.classList.remove("hidden");
  });

  window.addEventListener("load", async () => {
    try {
      const [auth, replays] = await Promise.all([request("/api/auth/me"), request("/api/replays")]);
      currentUser = auth.isAuthenticated ? auth.user : null;
      renderMember();
      renderReplays(replays);
    } catch {
      elements.empty.classList.remove("hidden");
    }
  });
})();
