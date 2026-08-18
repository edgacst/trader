(() => {
  const video = document.getElementById("liveVideo");
  const overlay = document.getElementById("videoOverlay");
  const overlayTitle = document.getElementById("overlayTitle");
  const overlayMessage = document.getElementById("overlayMessage");
  const retryButton = document.getElementById("retryButton");
  const connectingPill = document.getElementById("connectingPill");
  const liveBadge = document.getElementById("liveBadge");
  const viewerCount = document.getElementById("viewerCount");
  const detailStatus = document.getElementById("detailStatus");
  const qualityText = document.getElementById("qualityText");
  const theaterButton = document.getElementById("theaterButton");
  const fullscreenButton = document.getElementById("fullscreenButton");
  const broadcastTitle = document.getElementById("broadcastTitle");
  const broadcastDescription = document.getElementById("broadcastDescription");
  const broadcastNotice = document.getElementById("broadcastNotice");
  const broadcastNoticeText = document.getElementById("broadcastNoticeText");
  const scheduleCaption = document.getElementById("scheduleCaption");
  const scheduleTime = document.getElementById("scheduleTime");
  const scheduleTitle = document.getElementById("scheduleTitle");
  const scheduleDescription = document.getElementById("scheduleDescription");

  let hls = null;
  let whep = null;
  let hlsUrl = "";
  let webrtcUrl = "";
  let isAttached = false;
  let playerMode = "none";
  let playerGeneration = 0;
  let webrtcRetryAfter = 0;
  let lastKnownLive = false;
  let lastLowLatencyReady = false;
  let hasLiveAccess = false;
  let liveAccessReason = "login";
  let broadcastInfo = null;
  let reminderTimer = null;

  const formatSchedule = value => {
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return null;
    return new Intl.DateTimeFormat("ko-KR", {
      month: "numeric",
      day: "numeric",
      weekday: "short",
      hour: "2-digit",
      minute: "2-digit"
    }).format(date);
  };

  const updateSchedule = isLive => {
    scheduleTitle.textContent = broadcastInfo?.title || "실시간 증권 차트 방송";
    if (isLive) {
      scheduleCaption.textContent = "현재 진행 중인 라이브";
      scheduleTime.textContent = "지금 LIVE";
      scheduleDescription.textContent = "회원 시청 화면으로 방송 중입니다.";
      const reminderButton = document.getElementById("scheduleReminderButton");
      if (reminderButton) {
        reminderButton.disabled = true;
        reminderButton.textContent = "방송 진행 중";
      }
      return;
    }

    const formatted = broadcastInfo?.scheduledAt ? formatSchedule(broadcastInfo.scheduledAt) : null;
    scheduleCaption.textContent = formatted ? "예정된 라이브" : "다음 방송 안내";
    scheduleTime.textContent = formatted || "일정 미정";
    scheduleDescription.textContent = formatted
      ? "예정 시간에 이 화면에서 자동 연결됩니다."
      : "방송 시작 시 자동 연결됩니다.";
    const reminderButton = document.getElementById("scheduleReminderButton");
    if (reminderButton) {
      const scheduledAt = broadcastInfo?.scheduledAt ? new Date(broadcastInfo.scheduledAt) : null;
      const reminderSaved = localStorage.getItem("stockcaster.scheduleReminder") === broadcastInfo?.scheduledAt;
      reminderButton.disabled = !scheduledAt || scheduledAt.getTime() <= Date.now();
      reminderButton.textContent = reminderSaved ? "방송 알림 켜짐" : "방송 알림 켜기";
      reminderButton.classList.toggle("active", reminderSaved);
    }
    scheduleBrowserReminder();
  };

  const scheduleBrowserReminder = () => {
    clearTimeout(reminderTimer);
    const value = broadcastInfo?.scheduledAt;
    if (!value || localStorage.getItem("stockcaster.scheduleReminder") !== value) return;
    const target = new Date(value).getTime();
    const remaining = target - Date.now();
    if (remaining <= 0) {
      localStorage.removeItem("stockcaster.scheduleReminder");
      return;
    }
    reminderTimer = setTimeout(() => {
      if (remaining > 2_000_000_000) scheduleBrowserReminder();
      else {
        if ("Notification" in window && Notification.permission === "granted")
          new Notification("StockCaster 방송 시작", { body: broadcastInfo?.title || "예정된 라이브 방송이 시작됩니다." });
        localStorage.removeItem("stockcaster.scheduleReminder");
        updateSchedule(lastKnownLive);
      }
    }, Math.min(remaining, 2_000_000_000));
  };

  const applyBroadcastInfo = info => {
    broadcastInfo = info;
    const title = info.title || "실시간 증권 차트 방송";
    broadcastTitle.textContent = title;
    broadcastDescription.textContent = info.description || "실시간 차트 분석과 시장 흐름을 전달합니다.";
    broadcastNoticeText.textContent = info.notice || "";
    broadcastNotice.classList.toggle("hidden", !info.notice);
    document.title = `${title} | StockCaster`;
    updateSchedule(lastKnownLive);
  };

  const loadBroadcastInfo = async () => {
    try {
      const response = await fetch("/api/live/info", { cache: "no-store" });
      if (!response.ok) throw new Error("broadcast info unavailable");
      applyBroadcastInfo(await response.json());
    } catch {
      updateSchedule(lastKnownLive);
    }
  };

  const showOverlay = (title, message) => {
    overlayTitle.textContent = title;
    overlayMessage.textContent = message;
    overlay.classList.remove("hidden");
  };

  const hideOverlay = () => overlay.classList.add("hidden");
  const setConnecting = (visible) => connectingPill.classList.toggle("visible", visible);

  const updateBadge = (isLive) => {
    liveBadge.classList.toggle("live", isLive);
    liveBadge.classList.toggle("offline", !isLive);
    liveBadge.querySelector("span").textContent = isLive ? "LIVE 방송 중" : "방송 대기";
    detailStatus.textContent = isLive ? "실시간 방송 중" : "방송 대기";
    retryButton.textContent = isLive ? "다시 연결" : "방송 상태 확인";
  };

  const detachPlayer = () => {
    playerGeneration += 1;
    if (whep) {
      whep.close();
      whep = null;
    }
    if (hls) {
      hls.destroy();
      hls = null;
    }
    video.pause();
    video.srcObject = null;
    video.removeAttribute("src");
    video.load();
    isAttached = false;
    playerMode = "none";
    setConnecting(false);
  };

  const loadLiveAccess = async () => {
    try {
      const response = await fetch("/api/live/config", { cache: "no-store" });
      if (!response.ok) {
        hasLiveAccess = false;
        liveAccessReason = response.status === 401 ? "login" : "denied";
        hlsUrl = "";
        webrtcUrl = "";
        if (isAttached) detachPlayer();
        return false;
      }

      const config = await response.json();
      hlsUrl = config.hlsUrl;
      webrtcUrl = config.webrtcUrl || "";
      hasLiveAccess = true;
      liveAccessReason = "granted";
      return true;
    } catch {
      hasLiveAccess = false;
      liveAccessReason = "unavailable";
      hlsUrl = "";
      webrtcUrl = "";
      return false;
    }
  };

  const showAccessOverlay = () => {
    if (liveAccessReason === "login") {
      showOverlay("회원 로그인이 필요합니다", "무료회원도 로그인하면 라이브 방송을 시청할 수 있습니다.");
      retryButton.textContent = "로그인하고 시청";
    } else if (liveAccessReason === "denied") {
      showOverlay("시청 권한이 없습니다", "운영자에게 회원 등급과 시청 권한을 확인해 주세요.");
      retryButton.textContent = "시청 권한 확인";
    } else {
      showOverlay("재생 정보를 불러오지 못했습니다", "잠시 후 다시 시도해 주세요.");
      retryButton.textContent = "다시 확인";
    }
  };

  const attachHlsPlayer = () => {
    if (!hlsUrl || isAttached) return;
    isAttached = true;
    playerMode = "hls";
    setConnecting(true);
    showOverlay("안정 재생으로 연결하고 있습니다", "저지연 연결이 준비될 때까지 잠시만 기다려 주세요.");

    if (window.Hls && Hls.isSupported()) {
      hls = new Hls({
        lowLatencyMode: true,
        backBufferLength: 30,
        liveSyncDurationCount: 3,
        liveMaxLatencyDurationCount: 7,
        maxLiveSyncPlaybackRate: 1,
        maxBufferHole: 0.5
      });
      hls.loadSource(hlsUrl);
      hls.attachMedia(video);
      hls.on(Hls.Events.MANIFEST_PARSED, (_, data) => {
        const highest = data.levels?.reduce((best, level) => level.height > best.height ? level : best, { height: 0 });
        qualityText.textContent = highest?.height ? `HLS · 최대 ${highest.height}p` : "HLS · 자동";
        video.play().catch(() => {});
      });
      hls.on(Hls.Events.ERROR, (_, data) => {
        if (!data.fatal) return;
        if (data.type === Hls.ErrorTypes.NETWORK_ERROR) {
          setTimeout(() => hls?.startLoad(), 1200);
        } else if (data.type === Hls.ErrorTypes.MEDIA_ERROR) {
          hls.recoverMediaError();
        } else {
          detachPlayer();
          showOverlay("영상을 다시 연결하고 있습니다", "방송 상태를 확인한 뒤 자동으로 재시도합니다.");
        }
      });
    } else if (video.canPlayType("application/vnd.apple.mpegurl")) {
      video.src = hlsUrl;
      video.play().catch(() => {});
    } else {
      isAttached = false;
      showOverlay("이 브라우저에서 영상을 재생할 수 없습니다", "최신 Chrome, Edge 또는 Safari를 사용해 주세요.");
    }
  };

  const fallbackToHls = () => {
    if (playerMode !== "webrtc") return;
    detachPlayer();
    webrtcRetryAfter = Date.now() + 10000;
    attachHlsPlayer();
  };

  const attachWebRtcPlayer = async () => {
    if (!webrtcUrl || !window.StockCasterWhepPlayer || isAttached) {
      attachHlsPlayer();
      return;
    }

    isAttached = true;
    playerMode = "webrtc";
    const generation = ++playerGeneration;
    setConnecting(true);
    showOverlay("실시간 방송에 연결하고 있습니다", "WebRTC 저지연 연결을 준비하고 있습니다.");

    whep = new window.StockCasterWhepPlayer({
      url: webrtcUrl,
      video,
      onError: () => fallbackToHls()
    });

    try {
      await whep.start();
      if (generation !== playerGeneration || playerMode !== "webrtc") return;
      qualityText.textContent = "WebRTC · 실시간";
      video.play().catch(() => {});
    } catch {
      if (generation === playerGeneration && playerMode === "webrtc")
        fallbackToHls();
    }
  };

  const attachPlayer = () => {
    const canTryWebRtc = lastLowLatencyReady && webrtcUrl &&
      window.StockCasterWhepPlayer && Date.now() >= webrtcRetryAfter;
    if (canTryWebRtc)
      attachWebRtcPlayer();
    else
      attachHlsPlayer();
  };

  video.addEventListener("playing", () => {
    hideOverlay();
    setConnecting(false);
  });
  video.addEventListener("waiting", () => setConnecting(true));
  video.addEventListener("canplay", () => setConnecting(false));
  const pollStatus = async (manual = false) => {
    try {
      const response = await fetch("/api/live/status", { cache: "no-store" });
      if (!response.ok) throw new Error("status unavailable");
      const status = await response.json();
      lastKnownLive = Boolean(status.isLive);
      lastLowLatencyReady = Boolean(status.lowLatencyReady);
      viewerCount.textContent = status.viewerCount ?? 0;
      updateBadge(lastKnownLive);
      updateSchedule(lastKnownLive);

      if (!hasLiveAccess) {
        if (isAttached) detachPlayer();
        setConnecting(false);
        showAccessOverlay();
        return lastKnownLive;
      }

      if (lastKnownLive) {
        if (playerMode === "webrtc" && !lastLowLatencyReady) {
          detachPlayer();
          attachHlsPlayer();
        } else if (playerMode === "hls" && lastLowLatencyReady &&
                   Date.now() >= webrtcRetryAfter) {
          detachPlayer();
          attachPlayer();
        } else if (!isAttached) {
          attachPlayer();
        }
      } else {
        if (isAttached) detachPlayer();
        lastLowLatencyReady = false;
        webrtcRetryAfter = 0;
        qualityText.textContent = "자동";
        showOverlay(
          manual ? "현재 송출 중인 방송이 없습니다" : "방송을 준비하고 있습니다",
          manual
            ? "StockCaster Live에서 방송 시작을 누르면 자동으로 연결됩니다."
            : "방송이 시작되면 영상이 자동으로 재생됩니다."
        );
      }
      return lastKnownLive;
    } catch {
      lastKnownLive = false;
      updateBadge(false);
      showOverlay(
        manual ? "방송 서버에 연결할 수 없습니다" : "방송 서버에 연결하는 중입니다",
        manual ? "서버 실행 상태를 확인한 뒤 다시 시도해 주세요." : "잠시 후 자동으로 다시 확인합니다."
      );
      return false;
    }
  };

  retryButton.addEventListener("click", async () => {
    if (retryButton.disabled) return;

    if (!hasLiveAccess && liveAccessReason === "login") {
      document.getElementById("memberButton")?.click();
      return;
    }

    retryButton.disabled = true;
    retryButton.textContent = "확인 중...";
    setConnecting(true);
    showOverlay("방송 상태를 다시 확인하고 있습니다", "잠시만 기다려 주세요.");

    if (isAttached) detachPlayer();
    try {
      await loadLiveAccess();
      await pollStatus(true);
    } finally {
      retryButton.disabled = false;
      if (hasLiveAccess) retryButton.textContent = lastKnownLive ? "다시 연결" : "다시 확인";
      else showAccessOverlay();
      if (!lastKnownLive) setConnecting(false);
    }
  });

  theaterButton.addEventListener("click", () => {
    const enabled = document.body.classList.toggle("theater-mode");
    theaterButton.setAttribute("aria-pressed", String(enabled));
    theaterButton.textContent = enabled ? "▤ 채팅 화면으로" : "▣ 초대형 화면";
    window.scrollTo({ top: 0, behavior: "smooth" });
  });

  fullscreenButton.addEventListener("click", async () => {
    try {
      if (document.fullscreenElement) {
        await document.exitFullscreen();
      } else {
        await document.getElementById("videoStage").requestFullscreen();
      }
    } catch {
      showOverlay("전체화면을 열지 못했습니다", "브라우저 메뉴의 전체화면 기능을 이용해 주세요.");
    }
  });

  document.getElementById("scheduleReminderButton")?.addEventListener("click", async event => {
    const value = broadcastInfo?.scheduledAt;
    if (!value) return;
    if (!("Notification" in window)) {
      showOverlay("알림을 지원하지 않는 브라우저입니다", "방송 일정을 직접 확인해 주세요.");
      return;
    }
    const permission = await Notification.requestPermission();
    if (permission !== "granted") {
      showOverlay("알림 권한이 필요합니다", "브라우저 주소창의 알림 권한을 허용해 주세요.");
      return;
    }
    localStorage.setItem("stockcaster.scheduleReminder", value);
    event.currentTarget.textContent = "방송 알림 켜짐";
    event.currentTarget.classList.add("active");
    scheduleBrowserReminder();
  });

  document.addEventListener("fullscreenchange", () => {
    const active = Boolean(document.fullscreenElement);
    fullscreenButton.textContent = active ? "✕" : "⛶";
    fullscreenButton.title = active ? "전체화면 종료" : "브라우저 전체화면";
    fullscreenButton.setAttribute("aria-label", fullscreenButton.title);
  });

  const initialize = async () => {
    await Promise.all([loadLiveAccess(), loadBroadcastInfo()]);
    await pollStatus();
    setInterval(pollStatus, 2500);
    setInterval(loadBroadcastInfo, 15000);
  };

  window.addEventListener("stockcaster:auth-changed", async event => {
    if (!event.detail?.user) {
      hasLiveAccess = false;
      liveAccessReason = "login";
      hlsUrl = "";
      webrtcUrl = "";
      if (isAttached) detachPlayer();
      showAccessOverlay();
      return;
    }

    await loadLiveAccess();
    await pollStatus();
  });

  window.addEventListener("load", initialize);
})();
