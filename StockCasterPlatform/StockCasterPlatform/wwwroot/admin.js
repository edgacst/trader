(() => {
  const loginCard = document.getElementById("adminLoginCard");
  const dashboard = document.getElementById("adminDashboard");
  const loginError = document.getElementById("adminLoginError");
  const toast = document.getElementById("adminToast");
  let currentUser = null;
  let refreshTimer = null;
  let chatPolicyDirty = false;

  const request = async (url, options = {}) => {
    const response = await fetch(url, {
      cache: "no-store",
      ...options,
      headers: { "Content-Type": "application/json", ...(options.headers || {}) }
    });
    const data = await response.json().catch(() => ({}));
    if (!response.ok) {
      const error = new Error(data.error || (response.status === 403 ? "운영자 권한이 필요합니다." : "요청을 처리하지 못했습니다."));
      error.status = response.status;
      throw error;
    }
    return data;
  };

  const showToast = message => {
    toast.textContent = message;
    toast.classList.add("visible");
    clearTimeout(showToast.timer);
    showToast.timer = setTimeout(() => toast.classList.remove("visible"), 1800);
  };

  const showLogin = message => {
    currentUser = null;
    loginCard.classList.remove("hidden");
    dashboard.classList.add("hidden");
    loginError.textContent = message || "";
    loginError.classList.toggle("hidden", !message);
    clearInterval(refreshTimer);
  };

  const showDashboard = user => {
    currentUser = user;
    document.getElementById("adminIdentity").textContent = `${user.displayName} 님 · ${user.roleLabel || "관리자"}`;
    loginCard.classList.add("hidden");
    dashboard.classList.remove("hidden");
    document.querySelectorAll(".admin-admin-only").forEach(element =>
      element.classList.toggle("hidden", user.role !== "Admin"));
    clearInterval(refreshTimer);
    refreshTimer = setInterval(loadDashboard, 4000);
  };

  const formatDate = value => new Intl.DateTimeFormat("ko-KR", {
    month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit", hour12: false
  }).format(new Date(value));

  const renderMembers = members => {
    const container = document.getElementById("memberList");
    container.replaceChildren();
    if (!members.length) {
      container.innerHTML = '<div class="admin-empty">가입한 회원이 없습니다.</div>';
      return;
    }

    members.forEach(member => {
      const item = document.createElement("article");
      item.className = "member-row";
      const avatar = document.createElement("div");
      avatar.className = "member-avatar";
      avatar.textContent = member.displayName.slice(0, 2).toUpperCase();
      const copy = document.createElement("div");
      copy.className = "member-copy";
      const title = document.createElement("strong");
      title.textContent = member.displayName;
      if (member.roleLabel) {
        const adminBadge = document.createElement("i");
        adminBadge.textContent = member.roleLabel;
        title.append(" ", adminBadge);
      }
      const details = document.createElement("span");
      details.textContent = `@${member.username} · ${member.roleLabel || "일반 회원"} · ${member.tierLabel} · ${formatDate(member.createdAt)} 가입`;
      if (member.isMuted) {
        const restriction = member.mutedUntil ? `${formatDate(member.mutedUntil)}까지 채팅 제한` : "채팅 영구 제한";
        details.textContent += ` · ${restriction}`;
      }
      copy.append(title, details);
      item.append(avatar, copy);

      if (!member.isAdmin) {
        const actions = document.createElement("div");
        actions.className = "member-actions";

        const tier = document.createElement("select");
        tier.className = "tier-select";
        tier.setAttribute("aria-label", `${member.displayName} 회원 등급`);
        [{ value: "Free", label: "무료" }, { value: "Premium", label: "프리미엄" }].forEach(optionData => {
          const option = document.createElement("option");
          option.value = optionData.value;
          option.textContent = optionData.label;
          option.selected = member.tier === optionData.value;
          tier.append(option);
        });
        tier.addEventListener("change", () => setMemberTier(member, tier.value));

        const role = document.createElement("select");
        role.className = "role-select";
        role.setAttribute("aria-label", `${member.displayName} 권한`);
        [{ value: "Member", label: "일반 회원" }, { value: "Broadcaster", label: "방송 진행자" }, { value: "ChatModerator", label: "채팅 관리자" }].forEach(optionData => {
          const option = document.createElement("option");
          option.value = optionData.value;
          option.textContent = optionData.label;
          option.selected = member.role === optionData.value;
          role.append(option);
        });
        role.addEventListener("change", () => setMemberRole(member, role.value));

        const muteDuration = document.createElement("select");
        muteDuration.className = "mute-duration-select";
        muteDuration.setAttribute("aria-label", `${member.displayName} 채팅 제한 시간`);
        [
          { value: "10", label: "10분 제한" },
          { value: "60", label: "1시간 제한" },
          { value: "1440", label: "24시간 제한" },
          { value: "", label: "영구 제한" }
        ].forEach(optionData => {
          const option = document.createElement("option");
          option.value = optionData.value;
          option.textContent = optionData.label;
          muteDuration.append(option);
        });
        muteDuration.disabled = member.isMuted;

        const muteButton = document.createElement("button");
        muteButton.className = `moderation-button${member.isMuted ? " muted" : ""}`;
        muteButton.textContent = member.isMuted ? "제한 해제" : "채팅 제한";
        muteButton.addEventListener("click", () => setMuted(
          member,
          !member.isMuted,
          muteDuration.value ? Number(muteDuration.value) : null));

        const deleteButton = document.createElement("button");
        deleteButton.className = "member-delete-button";
        deleteButton.textContent = "회원 삭제";
        deleteButton.addEventListener("click", () => deleteMember(member));

        actions.append(tier, role, muteDuration, muteButton, deleteButton);
        item.append(actions);
      }
      container.append(item);
    });
  };

  const renderMessages = messages => {
    const container = document.getElementById("adminMessageList");
    container.replaceChildren();
    if (!messages.length) {
      container.innerHTML = '<div class="admin-empty">아직 채팅 메시지가 없습니다.</div>';
      return;
    }

    [...messages].reverse().forEach(message => {
      const item = document.createElement("article");
      item.className = "admin-message-row";
      const content = document.createElement("div");
      const meta = document.createElement("div");
      meta.className = "admin-message-meta";
      const name = document.createElement("strong");
      name.textContent = message.displayName;
      const time = document.createElement("time");
      time.textContent = formatDate(message.sentAt);
      meta.append(name, time);
      const text = document.createElement("p");
      text.textContent = message.text;
      content.append(meta, text);
      const remove = document.createElement("button");
      remove.className = "delete-message-button";
      remove.type = "button";
      remove.textContent = "삭제";
      remove.addEventListener("click", () => deleteMessage(message.id));
      item.append(content, remove);
      container.append(item);
    });
  };

  const renderReplays = replays => {
    const container = document.getElementById("adminReplayList");
    container.replaceChildren();
    if (!replays.length) {
      container.innerHTML = '<div class="admin-empty">방송을 종료하면 녹화 영상이 자동으로 표시됩니다.</div>';
      return;
    }

    replays.forEach(replay => {
      const row = document.createElement("article");
      row.className = "admin-replay-row";
      const image = document.createElement("img");
      image.src = replay.thumbnailUrl;
      image.alt = "";

      const fields = document.createElement("div");
      fields.className = "admin-replay-fields";
      const title = document.createElement("input");
      title.value = replay.title;
      title.maxLength = 80;
      title.setAttribute("aria-label", `${replay.title} 제목`);
      const meta = document.createElement("span");
      meta.textContent = `${formatDate(replay.startedAt)} · ${formatReplayDuration(replay.durationSeconds)}`;
      fields.append(title, meta);

      const actions = document.createElement("div");
      actions.className = "admin-replay-actions";
      const publishLabel = document.createElement("label");
      publishLabel.className = "publish-toggle";
      const published = document.createElement("input");
      published.type = "checkbox";
      published.checked = replay.isPublished;
      publishLabel.append(published, document.createTextNode("회원 공개"));
      const save = document.createElement("button");
      save.className = "replay-save-button";
      save.type = "button";
      save.textContent = "저장";
      save.addEventListener("click", () => saveReplay(replay.id, title.value, published.checked));
      const remove = document.createElement("button");
      remove.className = "delete-message-button";
      remove.type = "button";
      remove.textContent = "삭제";
      remove.addEventListener("click", () => deleteReplay(replay.id, replay.title));
      actions.append(publishLabel, save, remove);
      row.append(image, fields, actions);
      container.append(row);
    });
  };

  const formatReplayDuration = seconds => {
    const total = Math.max(0, Math.round(seconds));
    const hours = Math.floor(total / 3600);
    const minutes = Math.floor((total % 3600) / 60);
    const secs = total % 60;
    return hours ? `${hours}시간 ${minutes}분` : `${minutes}분 ${secs}초`;
  };

  const renderTickerMessages = messages => {
    const container = document.getElementById("tickerMessageList");
    container.replaceChildren();
    document.getElementById("tickerMessageCount").textContent = `${messages.length} / 20개`;
    if (!messages.length) {
      container.innerHTML = '<div class="admin-empty">등록된 스크롤 공지가 없습니다.</div>';
      return;
    }

    messages.forEach(message => {
      const item = document.createElement("article");
      item.className = "ticker-admin-item";
      const copy = document.createElement("div");
      const text = document.createElement("p");
      text.textContent = message.text;
      const time = document.createElement("time");
      time.textContent = `${formatDate(message.createdAt)} 등록`;
      copy.append(text, time);
      const remove = document.createElement("button");
      remove.className = "delete-message-button";
      remove.type = "button";
      remove.textContent = "삭제";
      remove.addEventListener("click", () => deleteTickerMessage(message));
      item.append(copy, remove);
      container.append(item);
    });
  };

  const renderReports = reports => {
    const container = document.getElementById("reportList");
    container.replaceChildren();
    document.getElementById("reportCount").textContent = `${reports.filter(report => report.status === "Open").length}건 처리 대기`;
    if (!reports.length) {
      container.innerHTML = '<div class="admin-empty">접수된 신고가 없습니다.</div>';
      return;
    }
    reports.forEach(report => {
      const row = document.createElement("article");
      row.className = `moderation-row ${report.status.toLowerCase()}`;
      const copy = document.createElement("div");
      const title = document.createElement("strong");
      title.textContent = `${report.category} · ${report.status === "Open" ? "처리 대기" : report.status === "Resolved" ? "처리 완료" : "기각"}`;
      const details = document.createElement("p");
      details.textContent = `${report.reporterName} 신고 · ${report.targetMemberName || "대상 미상"}${report.details ? ` · ${report.details}` : ""}`;
      const time = document.createElement("time");
      time.textContent = formatDate(report.createdAt);
      copy.append(title, details, time);
      const action = document.createElement("button");
      action.className = "moderation-button";
      action.textContent = report.status === "Open" ? "처리 완료" : "다시 열기";
      action.addEventListener("click", async () => {
        await request(`/api/admin/reports/${report.id}`, { method: "PUT", body: JSON.stringify({ status: report.status === "Open" ? "Resolved" : "Open" }) });
        showToast("신고 상태를 변경했습니다.");
        await loadDashboard();
      });
      row.append(copy, action);
      container.append(row);
    });
  };

  const renderActivityLogs = logs => {
    const container = document.getElementById("activityLogList");
    container.replaceChildren();
    if (!logs.length) {
      container.innerHTML = '<div class="admin-empty">활동 로그가 없습니다.</div>';
      return;
    }
    logs.slice(0, 100).forEach(log => {
      const row = document.createElement("div");
      row.className = "activity-log-row";
      const actor = document.createElement("strong");
      actor.textContent = log.actorName;
      const detail = document.createElement("span");
      detail.textContent = `${log.action} · ${log.details || log.target}`;
      const time = document.createElement("time");
      time.textContent = formatDate(log.createdAt);
      row.append(actor, detail, time);
      container.append(row);
    });
  };

  const renderChatPolicy = policy => {
    if (!chatPolicyDirty) {
      document.getElementById("chatPinnedNoticeInput").value = policy.pinnedNotice || "";
      document.getElementById("chatBlockedWordsInput").value = (policy.blockedWords || []).join(", ");
      document.getElementById("chatSlowModeSelect").value = String(policy.slowModeSeconds || 0);
    }
    document.getElementById("chatPolicySavedAt").textContent = policy.updatedAt
      ? `마지막 저장 ${formatDate(policy.updatedAt)}`
      : "저장 기록 없음";
  };

  const saveReplay = async (id, title, isPublished) => {
    await request(`/api/admin/replays/${id}`, {
      method: "PUT",
      body: JSON.stringify({ title, isPublished })
    });
    showToast("다시보기 설정을 저장했습니다.");
    await loadDashboard();
  };

  const deleteReplay = async (id, title) => {
    if (!confirm(`'${title}' 녹화 영상을 완전히 삭제할까요?`)) return;
    await request(`/api/admin/replays/${id}`, { method: "DELETE" });
    showToast("다시보기 영상을 삭제했습니다.");
    await loadDashboard();
  };

  const loadDashboard = async () => {
    try {
      if (currentUser?.role === "ChatModerator") {
        const [messages, chatPolicy, reports] = await Promise.all([
          request("/api/admin/messages"),
          request("/api/admin/chat/policy"),
          request("/api/admin/reports")
        ]);
        document.getElementById("messageStat").textContent = messages.length;
        renderMessages(messages);
        renderChatPolicy(chatPolicy);
        renderReports(reports);
        return;
      }
      const [summary, members, messages, replays, security, chatPolicy, tickerMessages, reports, activityLogs] = await Promise.all([
        request("/api/admin/summary"),
        request("/api/admin/members"),
        request("/api/admin/messages"),
        request("/api/admin/replays"),
        request("/api/studio/config"),
        request("/api/admin/chat/policy"),
        request("/api/admin/ticker"),
        request("/api/admin/reports"),
        request("/api/admin/activity-logs")
      ]);
      document.getElementById("memberStat").textContent = summary.memberCount;
      document.getElementById("premiumStat").textContent = summary.premiumCount;
      document.getElementById("onlineStat").textContent = summary.onlineCount;
      document.getElementById("messageStat").textContent = summary.messageCount;
      document.getElementById("mutedStat").textContent = summary.mutedCount;
      document.getElementById("replayStat").textContent = summary.replayCount;
      document.getElementById("adminStreamKey").value = security.streamKey;
      renderMembers(members);
      renderMessages(messages);
      renderReplays(replays);
      renderChatPolicy(chatPolicy);
      renderTickerMessages(tickerMessages);
      renderReports(reports);
      renderActivityLogs(activityLogs);
    } catch (error) {
      if (error.status === 401 || error.status === 403) showLogin(error.message);
    }
  };

  const setMuted = async (member, muted, durationMinutes = null) => {
    await request(`/api/admin/members/${member.id}/mute`, {
      method: "POST",
      body: JSON.stringify({ muted, durationMinutes })
    });
    const durationLabel = durationMinutes === 10 ? "10분" : durationMinutes === 60 ? "1시간" : durationMinutes === 1440 ? "24시간" : "영구";
    showToast(muted ? `${member.displayName} 님의 채팅을 ${durationLabel} 제한했습니다.` : "채팅 제한을 해제했습니다.");
    await loadDashboard();
  };

  const setMemberTier = async (member, tier) => {
    await request(`/api/admin/members/${member.id}/tier`, {
      method: "PUT",
      body: JSON.stringify({ tier })
    });
    showToast(`${member.displayName} 님을 ${tier === "Premium" ? "프리미엄" : "무료"} 회원으로 변경했습니다.`);
    await loadDashboard();
  };

  const setMemberRole = async (member, role) => {
    await request(`/api/admin/members/${member.id}/role`, {
      method: "PUT",
      body: JSON.stringify({ role })
    });
    showToast(`${member.displayName} 님의 권한을 변경했습니다.`);
    await loadDashboard();
  };

  const deleteMember = async member => {
    if (!confirm(`${member.displayName} 회원을 삭제할까요? 삭제한 계정은 복구할 수 없습니다.`)) return;
    await request(`/api/admin/members/${member.id}`, { method: "DELETE" });
    showToast(`${member.displayName} 회원을 삭제했습니다.`);
    await loadDashboard();
  };

  const deleteMessage = async messageId => {
    await request(`/api/admin/messages/${messageId}`, { method: "DELETE" });
    showToast("메시지를 삭제했습니다.");
    await loadDashboard();
  };

  const deleteTickerMessage = async message => {
    if (!confirm(`'${message.text}' 스크롤 공지를 삭제할까요?`)) return;
    await request(`/api/admin/ticker/${message.id}`, { method: "DELETE" });
    showToast("스크롤 공지를 삭제했습니다.");
    await loadDashboard();
  };

  document.getElementById("tickerMessageForm").addEventListener("submit", async event => {
    event.preventDefault();
    const input = document.getElementById("tickerMessageInput");
    const errorElement = document.getElementById("tickerMessageError");
    const saveButton = document.getElementById("addTickerMessageButton");
    errorElement.classList.add("hidden");
    saveButton.disabled = true;
    saveButton.textContent = "등록 중...";
    try {
      await request("/api/admin/ticker", {
        method: "POST",
        body: JSON.stringify({ text: input.value })
      });
      input.value = "";
      showToast("스크롤 공지를 등록했습니다.");
      await loadDashboard();
    } catch (error) {
      errorElement.textContent = error.message;
      errorElement.classList.remove("hidden");
    } finally {
      saveButton.disabled = false;
      saveButton.textContent = "공지 등록";
    }
  });

  document.querySelectorAll("#chatPolicyForm textarea, #chatPolicyForm select").forEach(input => {
    input.addEventListener("input", () => { chatPolicyDirty = true; });
    input.addEventListener("change", () => { chatPolicyDirty = true; });
  });

  document.getElementById("chatPolicyForm").addEventListener("submit", async event => {
    event.preventDefault();
    const errorElement = document.getElementById("chatPolicyError");
    const saveButton = document.getElementById("saveChatPolicyButton");
    errorElement.classList.add("hidden");
    saveButton.disabled = true;
    saveButton.textContent = "저장 중...";
    try {
      const blockedWords = document.getElementById("chatBlockedWordsInput").value
        .split(/[\n,]+/)
        .map(word => word.trim())
        .filter(Boolean);
      const policy = await request("/api/admin/chat/policy", {
        method: "PUT",
        body: JSON.stringify({
          pinnedNotice: document.getElementById("chatPinnedNoticeInput").value,
          slowModeSeconds: Number(document.getElementById("chatSlowModeSelect").value),
          blockedWords
        })
      });
      chatPolicyDirty = false;
      renderChatPolicy(policy);
      showToast("채팅 운영정책을 저장하고 실시간 적용했습니다.");
    } catch (error) {
      errorElement.textContent = error.message;
      errorElement.classList.remove("hidden");
    } finally {
      saveButton.disabled = false;
      saveButton.textContent = "정책 저장";
    }
  });

  document.getElementById("adminLoginForm").addEventListener("submit", async event => {
    event.preventDefault();
    loginError.classList.add("hidden");
    try {
      const result = await request("/api/auth/login", {
        method: "POST",
        body: JSON.stringify({
          username: document.getElementById("adminUsername").value,
          password: document.getElementById("adminPassword").value
        })
      });
      if (!["Admin", "ChatModerator"].includes(result.user.role)) {
        await request("/api/auth/logout", { method: "POST", body: "{}" });
        throw new Error("관리 권한이 있는 계정으로 로그인해 주세요.");
      }
      showDashboard(result.user);
      await loadDashboard();
    } catch (error) {
      showLogin(error.message);
    }
  });

  document.getElementById("adminRefreshButton").addEventListener("click", loadDashboard);
  document.getElementById("adminPasswordForm").addEventListener("submit", async event => {
    event.preventDefault();
    const errorElement = document.getElementById("adminPasswordError");
    const currentPassword = document.getElementById("currentAdminPassword").value;
    const newPassword = document.getElementById("newAdminPassword").value;
    const confirmation = document.getElementById("confirmAdminPassword").value;
    errorElement.classList.add("hidden");

    if (newPassword !== confirmation) {
      errorElement.textContent = "새 비밀번호 확인이 일치하지 않습니다.";
      errorElement.classList.remove("hidden");
      return;
    }

    try {
      await request("/api/admin/password", {
        method: "POST",
        body: JSON.stringify({ currentPassword, newPassword })
      });
      event.currentTarget.reset();
      showToast("운영자 비밀번호를 변경했습니다.");
    } catch (error) {
      errorElement.textContent = error.message;
      errorElement.classList.remove("hidden");
    }
  });

  document.getElementById("toggleAdminStreamKey").addEventListener("click", event => {
    const input = document.getElementById("adminStreamKey");
    const visible = input.type === "text";
    input.type = visible ? "password" : "text";
    event.currentTarget.textContent = visible ? "보기" : "숨기기";
  });

  document.getElementById("copyAdminStreamKey").addEventListener("click", async () => {
    await navigator.clipboard.writeText(document.getElementById("adminStreamKey").value);
    showToast("송출 보안키를 복사했습니다.");
  });

  document.getElementById("rotateBroadcastKey").addEventListener("click", async event => {
    if (!confirm("새 송출 보안키를 발급할까요? 이전 키로는 다음 방송을 시작할 수 없습니다.")) return;
    event.currentTarget.disabled = true;
    try {
      const result = await request("/api/admin/broadcast-key/rotate", { method: "POST", body: "{}" });
      document.getElementById("adminStreamKey").value = result.streamKey;
      showToast("새 송출 보안키를 발급했습니다.");
    } catch (error) {
      showToast(error.message);
    } finally {
      event.currentTarget.disabled = false;
    }
  });

  document.getElementById("adminLogoutButton").addEventListener("click", async () => {
    await request("/api/auth/logout", { method: "POST", body: "{}" });
    showLogin();
  });

  window.addEventListener("load", async () => {
    try {
      const result = await request("/api/auth/me");
      if (result.isAuthenticated && ["Admin", "ChatModerator"].includes(result.user.role)) {
        showDashboard(result.user);
        await loadDashboard();
      } else {
        showLogin(result.isAuthenticated ? "관리 권한이 있는 계정으로 로그인해 주세요." : "");
      }
    } catch { showLogin(); }
  });
})();
