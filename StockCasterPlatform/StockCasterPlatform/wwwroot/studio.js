(() => {
  const serverBadge = document.getElementById("serverBadge");
  const stateOrb = document.getElementById("stateOrb");
  const stateTitle = document.getElementById("stateTitle");
  const stateDescription = document.getElementById("stateDescription");
  const healthBadge = document.getElementById("streamHealthBadge");
  const toast = document.getElementById("copyToast");
  const infoForm = document.getElementById("broadcastInfoForm");
  const infoError = document.getElementById("broadcastInfoError");
  const saveInfoButton = document.getElementById("saveBroadcastInfoButton");
  const studioAlert = document.getElementById("studioAlert");

  const requestJson = async (url, options = {}) => {
    const response = await fetch(url, {
      cache: "no-store",
      headers: { "Content-Type": "application/json", ...(options.headers || {}) },
      ...options
    });
    const body = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(body.error || "요청을 처리하지 못했습니다.");
    return body;
  };

  const formatDateTime = value => {
    if (!value) return "저장 기록 없음";
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return "저장 기록 없음";
    return `마지막 저장 ${new Intl.DateTimeFormat("ko-KR", {
      month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit"
    }).format(date)}`;
  };

  const toLocalInputValue = value => {
    if (!value) return "";
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return "";
    const local = new Date(date.getTime() - date.getTimezoneOffset() * 60000);
    return local.toISOString().slice(0, 16);
  };

  const formatDuration = totalSeconds => {
    const seconds = Math.max(0, Number(totalSeconds) || 0);
    const hours = Math.floor(seconds / 3600);
    const minutes = Math.floor((seconds % 3600) / 60);
    const remainder = Math.floor(seconds % 60);
    return [hours, minutes, remainder].map(value => String(value).padStart(2, "0")).join(":");
  };

  const formatBytes = value => {
    let bytes = Math.max(0, Number(value) || 0);
    const units = ["B", "KB", "MB", "GB", "TB"];
    let unit = 0;
    while (bytes >= 1024 && unit < units.length - 1) {
      bytes /= 1024;
      unit += 1;
    }
    const digits = unit === 0 ? 0 : bytes >= 100 ? 0 : bytes >= 10 ? 1 : 2;
    return `${bytes.toFixed(digits)} ${units[unit]}`;
  };

  const formatBitrate = value => {
    const kbps = Number(value);
    if (!Number.isFinite(kbps)) return "측정 준비";
    return kbps >= 1000 ? `${(kbps / 1000).toFixed(2)} Mbps` : `${kbps.toFixed(0)} kbps`;
  };

  const loadConfig = async () => {
    const response = await fetch("/api/studio/config", { cache: "no-store" });
    if (!response.ok) {
      document.getElementById("rtmpUrl").value = "관리자 로그인 필요";
      document.getElementById("streamKey").value = "관리자 로그인 필요";
      document.getElementById("studioAuthNotice").textContent = "송출 연결 정보는 운영자 또는 방송 진행자에게만 표시됩니다. 먼저 권한 계정으로 로그인해 주세요.";
      throw new Error("admin authentication required");
    }
    const config = await response.json();
    document.getElementById("rtmpUrl").value = config.rtmpServerUrl;
    document.getElementById("streamKey").value = config.streamKey;
    document.getElementById("studioAuthNotice").textContent = "이 키가 없는 송출 연결은 서버에서 차단됩니다. 키를 다른 사람에게 공개하지 마세요.";
  };

  const applyBroadcastInfo = info => {
    document.getElementById("broadcastTitleInput").value = info.title || "";
    document.getElementById("broadcastDescriptionInput").value = info.description || "";
    document.getElementById("broadcastNoticeInput").value = info.notice || "";
    document.getElementById("broadcastScheduleInput").value = toLocalInputValue(info.scheduledAt);
    document.getElementById("broadcastInfoSavedAt").textContent = formatDateTime(info.updatedAt);
  };

  const loadBroadcastInfo = async () => {
    const info = await requestJson("/api/live/info");
    applyBroadcastInfo(info);
  };

  const updateHealth = status => {
    const health = status.health || "offline";
    const labels = { good: "송출 양호", starting: "측정 중", warning: "확인 필요", offline: "오프라인" };
    healthBadge.className = `stream-health ${health}`;
    healthBadge.textContent = labels[health] || "확인 중";

    if (!status.isLive) {
      stateDescription.textContent = "송출 프로그램 연결을 기다리고 있습니다.";
      return;
    }

    const issues = [];
    if (!status.videoCodec) issues.push("영상 없음");
    if (!status.audioCodec) issues.push("오디오 없음");
    if ((status.inboundFramesInError ?? 0) > 0) issues.push("오류 프레임 발생");
    stateDescription.textContent = issues.length
      ? `${issues.join(" · ")} — 송출 설정을 확인해 주세요.`
      : health === "starting"
        ? "연결되었습니다. 수신 속도를 계산하고 있습니다."
        : "영상과 음성이 정상적으로 수신되고 자동 녹화 중입니다.";
  };

  const loadStatus = async () => {
    try {
      const [healthResponse, statusResponse] = await Promise.all([
        fetch("/health", { cache: "no-store" }),
        fetch("/api/live/status", { cache: "no-store" })
      ]);
      if (!healthResponse.ok || !statusResponse.ok) throw new Error("status unavailable");
      const health = await healthResponse.json();
      const status = await statusResponse.json();
      const serverReady = health.mediaServer === "ready";
      serverBadge.className = `server-badge ${serverReady ? "ready" : "starting"}`;
      serverBadge.querySelector("span").textContent = serverReady ? "서버 정상" : "서버 시작 중";

      stateOrb.classList.toggle("live", status.isLive);
      stateTitle.textContent = status.isLive ? "LIVE 방송 중" : "방송 대기";
      updateHealth(status);
      const alertResponse = await fetch("/api/studio/alerts", { cache: "no-store" });
      if (alertResponse.ok) {
        const alert = await alertResponse.json();
        studioAlert.textContent = alert?.message || "";
        studioAlert.className = `studio-alert ${alert?.state || ""} ${alert?.isActive ? "visible" : "hidden"}`;
      }

      document.getElementById("studioViewerCount").textContent = `${status.viewerCount ?? 0}명`;
      document.getElementById("streamUptime").textContent = formatDuration(status.uptimeSeconds);
      document.getElementById("inboundBitrate").textContent = status.isLive ? formatBitrate(status.inboundBitrateKbps) : "-";
      document.getElementById("inboundData").textContent = formatBytes(status.inboundBytes);
      document.getElementById("inboundErrors").textContent = Number(status.inboundFramesInError || 0).toLocaleString("ko-KR");
      document.getElementById("recordingStatus").textContent = status.isRecording ? "녹화 중" : "대기";

      const resolution = status.videoWidth && status.videoHeight ? ` · ${status.videoWidth}×${status.videoHeight}` : "";
      document.getElementById("videoFormat").textContent = status.videoCodec ? `${status.videoCodec}${resolution}` : "-";
      const audioDetails = status.audioSampleRate
        ? ` · ${(status.audioSampleRate / 1000).toFixed(status.audioSampleRate % 1000 ? 1 : 0)}kHz${status.audioChannels ? ` · ${status.audioChannels}ch` : ""}`
        : "";
      document.getElementById("audioFormat").textContent = status.audioCodec ? `${status.audioCodec}${audioDetails}` : "-";
    } catch {
      serverBadge.className = "server-badge error";
      serverBadge.querySelector("span").textContent = "서버 확인 필요";
      healthBadge.className = "stream-health offline";
      healthBadge.textContent = "연결 오류";
    }
  };

  infoForm.addEventListener("submit", async event => {
    event.preventDefault();
    infoError.classList.add("hidden");
    saveInfoButton.disabled = true;
    saveInfoButton.textContent = "저장 중...";
    try {
      const scheduleValue = document.getElementById("broadcastScheduleInput").value;
      const scheduledAt = scheduleValue ? new Date(scheduleValue).toISOString() : null;
      const info = await requestJson("/api/admin/live/info", {
        method: "PUT",
        body: JSON.stringify({
          title: document.getElementById("broadcastTitleInput").value,
          description: document.getElementById("broadcastDescriptionInput").value,
          notice: document.getElementById("broadcastNoticeInput").value,
          scheduledAt
        })
      });
      applyBroadcastInfo(info);
      toast.textContent = "방송 안내를 저장했습니다";
      toast.classList.add("visible");
      setTimeout(() => toast.classList.remove("visible"), 1700);
    } catch (error) {
      infoError.textContent = error.message;
      infoError.classList.remove("hidden");
    } finally {
      saveInfoButton.disabled = false;
      saveInfoButton.textContent = "방송 안내 저장";
    }
  });

  document.getElementById("clearScheduleButton").addEventListener("click", () => {
    document.getElementById("broadcastScheduleInput").value = "";
  });

  document.querySelectorAll("[data-copy]").forEach(button => {
    button.addEventListener("click", async () => {
      const input = document.getElementById(button.dataset.copy);
      if (!input.value || input.value === "관리자 로그인 필요") return;
      await navigator.clipboard.writeText(input.value);
      toast.textContent = "복사했습니다";
      toast.classList.add("visible");
      setTimeout(() => toast.classList.remove("visible"), 1500);
    });
  });

  document.getElementById("toggleStreamKey").addEventListener("click", event => {
    const input = document.getElementById("streamKey");
    const visible = input.type === "text";
    input.type = visible ? "password" : "text";
    event.currentTarget.textContent = visible ? "보기" : "숨기기";
  });

  document.getElementById("refreshStatus").addEventListener("click", loadStatus);
  window.addEventListener("load", async () => {
    await Promise.allSettled([loadConfig(), loadBroadcastInfo()]);
    await loadStatus();
    setInterval(loadStatus, 2500);
  });
})();
