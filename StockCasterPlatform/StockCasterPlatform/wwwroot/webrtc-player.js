(() => {
  class StockCasterWhepPlayer {
    constructor({ url, video, onError }) {
      this.url = url;
      this.video = video;
      this.onError = onError;
      this.peer = null;
      this.sessionUrl = null;
      this.closed = false;
      this.disconnectTimer = null;
    }

    async start() {
      this.closed = false;
      const peer = new RTCPeerConnection();
      this.peer = peer;
      peer.addTransceiver("video", { direction: "recvonly" });
      peer.addTransceiver("audio", { direction: "recvonly" });

      peer.addEventListener("track", event => {
        if (this.closed) return;
        const stream = event.streams?.[0];
        if (stream) {
          this.video.srcObject = stream;
        } else {
          const current = this.video.srcObject instanceof MediaStream
            ? this.video.srcObject
            : new MediaStream();
          current.addTrack(event.track);
          this.video.srcObject = current;
        }
        this.video.play().catch(() => {});
      });

      peer.addEventListener("connectionstatechange", () => {
        if (this.closed) return;
        if (peer.connectionState === "connected") {
          clearTimeout(this.disconnectTimer);
          this.disconnectTimer = null;
        } else if (peer.connectionState === "failed") {
          this.fail(new Error("WebRTC 연결에 실패했습니다."));
        } else if (peer.connectionState === "disconnected" && !this.disconnectTimer) {
          this.disconnectTimer = setTimeout(() => {
            if (peer.connectionState === "disconnected")
              this.fail(new Error("WebRTC 연결이 끊겼습니다."));
          }, 3000);
        }
      });

      const offer = await peer.createOffer();
      await peer.setLocalDescription(offer);
      await this.waitForIceGathering(peer);

      const response = await fetch(this.url, {
        method: "POST",
        headers: { "Content-Type": "application/sdp" },
        body: peer.localDescription.sdp,
        cache: "no-store"
      });
      if (!response.ok)
        throw new Error(`WebRTC 응답 오류 (${response.status})`);

      const location = response.headers.get("Location");
      if (location)
        this.sessionUrl = new URL(location, this.url).toString();

      const answer = await response.text();
      if (this.closed) return;
      await peer.setRemoteDescription({ type: "answer", sdp: answer });
    }

    waitForIceGathering(peer) {
      if (peer.iceGatheringState === "complete") return Promise.resolve();
      return new Promise(resolve => {
        const timeout = setTimeout(done, 2500);
        const changed = () => {
          if (peer.iceGatheringState === "complete") done();
        };
        function done() {
          clearTimeout(timeout);
          peer.removeEventListener("icegatheringstatechange", changed);
          resolve();
        }
        peer.addEventListener("icegatheringstatechange", changed);
      });
    }

    fail(error) {
      if (this.closed) return;
      this.onError?.(error);
    }

    close() {
      if (this.closed) return;
      this.closed = true;
      clearTimeout(this.disconnectTimer);
      this.disconnectTimer = null;
      const sessionUrl = this.sessionUrl;
      this.sessionUrl = null;
      if (sessionUrl)
        fetch(sessionUrl, { method: "DELETE", keepalive: true }).catch(() => {});
      this.peer?.close();
      this.peer = null;
    }
  }

  window.StockCasterWhepPlayer = StockCasterWhepPlayer;
})();
