(() => {
  const ticker = document.querySelector(".risk-ticker");
  const track = ticker?.querySelector(".ticker-track");
  if (!ticker || !track) return;

  const fallback = [{ text: "⚠ 투자위험 고지 | 본 방송의 정보는 투자 참고용이며 투자 권유나 수익을 보장하지 않습니다. 투자 판단과 그에 따른 손익 책임은 투자자 본인에게 있습니다." }];
  let lastSignature = "";

  const buildCopy = (messages, hidden) => {
    const copy = document.createElement("div");
    copy.className = "ticker-copy";
    if (hidden) copy.setAttribute("aria-hidden", "true");

    messages.forEach(message => {
      const item = document.createElement("span");
      item.className = "ticker-item";
      item.textContent = message.text;
      copy.append(item);
    });
    return copy;
  };

  const render = messages => {
    const valid = (messages || []).filter(message => message?.text?.trim());
    const signature = valid.map(message => `${message.id || ""}:${message.text.trim()}`).join("|");
    if (signature === lastSignature) return;
    lastSignature = signature;
    ticker.classList.toggle("hidden", valid.length === 0);
    if (!valid.length) return;

    const first = buildCopy(valid, false);
    const second = buildCopy(valid, true);
    track.replaceChildren(first, second);

    requestAnimationFrame(() => {
      const pixelsPerSecond = 62;
      const duration = Math.max(20, first.scrollWidth / pixelsPerSecond);
      ticker.style.setProperty("--ticker-duration", `${duration.toFixed(2)}s`);
    });
  };

  const refresh = () => fetch("/api/ticker", { cache: "no-store" })
    .then(response => response.ok ? response.json() : Promise.reject())
    .then(render)
    .catch(() => {});

  render(fallback);
  refresh();
  setInterval(refresh, 10000);
})();
