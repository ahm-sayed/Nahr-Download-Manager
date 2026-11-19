// Nahr Download Manager — Persistent IDM-Style Video & Audio Floating Bar + In-Browser Quality Picker (v1.4)
// - Docks a compact 24px bar next to EVERY playing video (HTML5, YouTube, HLS/m3u8 streaming sites like cinejoy.pk, & iframes).
// - Shows the detected resolution right on the bar and opens an IDM-style quality menu BEFORE sending to Nahr!

(() => {
  if (window.__nahrContentInjected) return;
  window.__nahrContentInjected = true;

  const IS_TOP = window === window.top;
  const IS_YOUTUBE = /(^|\.)youtube\.com$/i.test(location.hostname);
  const ICON_URL = chrome.runtime.getURL("icons/icon16.png");

  let detectedTabMedia = [];
  let iframePlayingInfo = null;
  let dismissedUntilUrlChange = false;
  let lastPageHref = location.href;
  let pinnedTarget = null;
  let isHoveringBar = false;
  let openMenuMode = null; // null | "video" | "audio"
  let fullscreenIdleTimer = null;

  const YT_VIDEO_PRESETS = [
    { key: "best", badge: "AUTO", label: "Best Available Quality (Auto / 4K)" },
    { key: "1080", badge: "FHD", label: "1080p Full HD (MP4)" },
    { key: "720", badge: "HD", label: "720p HD (MP4)" },
    { key: "480", badge: "SD", label: "480p Standard (MP4)" },
    { key: "360", badge: "LOW", label: "360p Low (MP4)" }
  ];

  const AUDIO_QUALITY_PRESETS = [
    { key: "mp3:320", badge: "320K", label: "MP3 — 320 kbps (High Quality)" },
    { key: "mp3:192", badge: "192K", label: "MP3 — 192 kbps (Standard)" },
    { key: "mp3:128", badge: "128K", label: "MP3 — 128 kbps (Small Size)" },
    { key: "m4a:orig", badge: "M4A", label: "M4A — Original / High Quality" },
    { key: "m4a:64", badge: "64K", label: "M4A — 64 kbps (Low Data)" }
  ];

  // ------------------------------------------------------------------ Relay MAIN-world HLS hook (including buffered document_start streams)
  window.addEventListener("message", (e) => {
    if (e.data && e.data.__nahrStreamDetected) {
      try {
        chrome.runtime.sendMessage({ type: "streamDetectedFromHook", stream: e.data });
      } catch {}
    }
  });

  // Request any streams that page-hook.js caught at document_start before content.js loaded
  try {
    window.postMessage({ __nahrRequestStreams: true }, "*");
  } catch {}

  // ------------------------------------------------------------------ Alt+Click bypass & Outside Click Close Menu
  document.addEventListener(
    "click",
    (e) => {
      if (e.altKey) {
        try {
          chrome.runtime.sendMessage({ type: "altBypass" });
        } catch {}
      }
      if (openMenuMode && !isHoveringBar) {
        closeQualityMenu();
      }
    },
    true
  );

  // ------------------------------------------------------------------ Background Stream & Iframe Notifications
  chrome.runtime.onMessage.addListener((msg) => {
    if (!msg) return;
    if (msg.type === "nahrMediaUpdated") {
      detectedTabMedia = msg.media || [];
      if (IS_TOP && !dismissedUntilUrlChange) {
        syncPersistentBar();
        if (openMenuMode === "video") renderQualityMenu("video");
      }
    } else if (msg.type === "nahrIframeVideoPlaying") {
      iframePlayingInfo = { iframeUrl: msg.iframeUrl };
      if (msg.media) detectedTabMedia = msg.media;
      if (IS_TOP && !dismissedUntilUrlChange) syncPersistentBar();
    }
  });

  try {
    chrome.runtime.sendMessage({ type: "getTabMedia" }, (res) => {
      if (!res) return;
      if (res.media && res.media.length > 0) detectedTabMedia = res.media;
      if (res.playingIframe) iframePlayingInfo = res.playingIframe;
      if (!dismissedUntilUrlChange) syncPersistentBar();
    });
  } catch {}

  // ------------------------------------------------------------------ Capture-Phase Media Playback Detection
  function onMediaActivity(e) {
    const el = e.target;
    if (!el || (el.tagName !== "VIDEO" && el.tagName !== "AUDIO")) return;
    if (el.tagName === "AUDIO" && el.duration && el.duration < 4) return;

    if (location.href !== lastPageHref) {
      lastPageHref = location.href;
      dismissedUntilUrlChange = false;
    }

    if (!IS_TOP) {
      try {
        chrome.runtime.sendMessage({ type: "iframeVideoPlaying", iframeUrl: location.href });
      } catch {}
    }

    if (!dismissedUntilUrlChange) {
      syncPersistentBar(el);
    }
  }

  document.addEventListener("play", onMediaActivity, true);
  document.addEventListener("playing", onMediaActivity, true);
  document.addEventListener("loadeddata", onMediaActivity, true);

  // ------------------------------------------------------------------ Compact 24px IDM-Style Mini Bar + Quality Dropdown Menu

  let hostEl = null;
  let shadow = null;
  let barEl = null;
  let menuEl = null;
  let videoBtn = null;
  let videoLabel = null;
  let audioBtn = null;
  let audioSep = null;
  let plBtn = null;
  let plSep = null;
  let closeBtn = null;
  let toastEl = null;

  function ensureMiniBar() {
    if (hostEl && document.documentElement.contains(hostEl)) return;

    hostEl = document.createElement("div");
    hostEl.style.cssText = "position:fixed;top:0;left:0;width:0;height:0;z-index:2147483647;pointer-events:none;";
    shadow = hostEl.attachShadow({ mode: "closed" });

    const style = document.createElement("style");
    style.textContent = `
      .idm-bar {
        position: fixed;
        display: none;
        align-items: center;
        height: 24px;
        background: linear-gradient(180deg, rgba(24, 34, 52, 0.94) 0%, rgba(15, 23, 42, 0.98) 100%);
        border: 1px solid rgba(56, 189, 248, 0.55);
        border-radius: 5px;
        box-shadow: 0 3px 10px rgba(0, 0, 0, 0.42);
        font-family: "Segoe UI", Tahoma, sans-serif;
        font-size: 11px;
        color: #f8fafc;
        pointer-events: auto;
        user-select: none;
        direction: ltr;
        opacity: 0.88;
        transition: opacity 0.15s ease;
        overflow: hidden;
      }
      .idm-bar:hover, .idm-bar.menu-open {
        opacity: 1;
        border-color: rgba(56, 189, 248, 0.95);
      }
      .idm-bar.visible {
        display: inline-flex;
      }
      .idm-action {
        display: inline-flex;
        align-items: center;
        gap: 5px;
        height: 100%;
        padding: 0 7px;
        border: none;
        background: transparent;
        color: #f8fafc;
        font-family: inherit;
        font-size: 11px;
        font-weight: 600;
        cursor: pointer;
        white-space: nowrap;
      }
      .idm-action:hover, .idm-action.active {
        background: rgba(37, 99, 235, 0.65);
      }
      .idm-audio {
        color: #7dd3fc;
        padding: 0 6px;
      }
      .idm-audio:hover, .idm-audio.active {
        background: rgba(14, 165, 233, 0.5);
        color: #ffffff;
      }
      .idm-pl {
        color: #c4b5fd;
        padding: 0 6px;
      }
      .idm-icon {
        width: 13px;
        height: 13px;
        border-radius: 3px;
        flex-shrink: 0;
      }
      .idm-caret {
        font-size: 9px;
        opacity: 0.8;
        margin-left: 1px;
      }
      .idm-sep {
        width: 1px;
        height: 13px;
        background: rgba(148, 163, 184, 0.28);
      }
      .idm-close {
        display: inline-flex;
        align-items: center;
        justify-content: center;
        width: 19px;
        height: 100%;
        border: none;
        background: transparent;
        color: #94a3b8;
        font-size: 12px;
        cursor: pointer;
        padding: 0;
      }
      .idm-close:hover {
        background: rgba(239, 68, 68, 0.78);
        color: #ffffff;
      }
      .idm-menu {
        position: fixed;
        display: none;
        flex-direction: column;
        min-width: 230px;
        max-width: 310px;
        background: rgba(15, 23, 42, 0.97);
        border: 1px solid rgba(56, 189, 248, 0.6);
        border-radius: 7px;
        box-shadow: 0 10px 26px rgba(0, 0, 0, 0.55);
        padding: 4px;
        pointer-events: auto;
        user-select: none;
        direction: ltr;
        font-family: "Segoe UI", Tahoma, sans-serif;
        z-index: 2147483647;
      }
      .idm-menu.open {
        display: flex;
      }
      .idm-menu-hdr {
        padding: 4px 8px 5px;
        font-size: 10px;
        font-weight: 700;
        text-transform: uppercase;
        letter-spacing: 0.4px;
        color: #94a3b8;
        border-bottom: 1px solid rgba(148, 163, 184, 0.18);
        margin-bottom: 3px;
        display: flex;
        justify-content: space-between;
        align-items: center;
      }
      .idm-menu-item {
        display: flex;
        align-items: center;
        gap: 8px;
        width: 100%;
        padding: 6px 8px;
        border: none;
        border-radius: 5px;
        background: transparent;
        color: #f1f5f9;
        font-family: inherit;
        font-size: 11.5px;
        font-weight: 500;
        text-align: left;
        cursor: pointer;
        transition: background 0.1s;
      }
      .idm-menu-item:hover {
        background: rgba(37, 99, 235, 0.65);
        color: #ffffff;
      }
      .idm-badge {
        display: inline-flex;
        align-items: center;
        justify-content: center;
        min-width: 36px;
        padding: 1.5px 5px;
        border-radius: 4px;
        font-size: 10px;
        font-weight: 700;
        background: rgba(56, 189, 248, 0.18);
        color: #38bdf8;
        border: 1px solid rgba(56, 189, 248, 0.35);
        flex-shrink: 0;
      }
      .idm-badge.audio {
        background: rgba(20, 184, 166, 0.2);
        color: #2dd4bf;
        border-color: rgba(20, 184, 166, 0.4);
      }
      .idm-item-lbl {
        flex: 1;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }
      .nahr-toast {
        position: fixed;
        bottom: 18px;
        right: 18px;
        display: none;
        padding: 7px 12px;
        border-radius: 7px;
        background: rgba(15, 23, 42, 0.94);
        color: #f8fafc;
        border: 1px solid rgba(56, 189, 248, 0.55);
        box-shadow: 0 6px 18px rgba(0, 0, 0, 0.35);
        font-family: "Segoe UI", sans-serif;
        font-size: 11.5px;
        pointer-events: none;
      }
      .nahr-toast.show { display: block; }
    `;

    barEl = document.createElement("div");
    barEl.className = "idm-bar";

    videoBtn = document.createElement("button");
    videoBtn.className = "idm-action";
    videoBtn.title = "Click to choose Video quality";
    videoBtn.innerHTML = `<img class="idm-icon" src="${ICON_URL}" alt=""/><span class="lbl">Video</span><span class="idm-caret">▾</span>`;
    videoLabel = videoBtn.querySelector(".lbl");

    audioSep = document.createElement("span");
    audioSep.className = "idm-sep";

    audioBtn = document.createElement("button");
    audioBtn.className = "idm-action idm-audio";
    audioBtn.title = "Click to choose Audio quality (MP3 / M4A)";
    audioBtn.innerHTML = `<span>♪ Audio</span><span class="idm-caret">▾</span>`;

    plSep = document.createElement("span");
    plSep.className = "idm-sep";
    plSep.style.display = "none";

    plBtn = document.createElement("button");
    plBtn.className = "idm-action idm-pl";
    plBtn.title = "Download Full Playlist";
    plBtn.textContent = "Playlist";
    plBtn.style.display = "none";

    const closeSep = document.createElement("span");
    closeSep.className = "idm-sep";

    closeBtn = document.createElement("button");
    closeBtn.className = "idm-close";
    closeBtn.title = "Hide";
    closeBtn.textContent = "×";

    menuEl = document.createElement("div");
    menuEl.className = "idm-menu";

    toastEl = document.createElement("div");
    toastEl.className = "nahr-toast";

    barEl.appendChild(videoBtn);
    barEl.appendChild(audioSep);
    barEl.appendChild(audioBtn);
    barEl.appendChild(plSep);
    barEl.appendChild(plBtn);
    barEl.appendChild(closeSep);
    barEl.appendChild(closeBtn);

    shadow.appendChild(style);
    shadow.appendChild(barEl);
    shadow.appendChild(menuEl);
    shadow.appendChild(toastEl);
    document.documentElement.appendChild(hostEl);

    const onEnter = () => {
      isHoveringBar = true;
      clearTimeout(fullscreenIdleTimer);
    };
    const onLeave = () => {
      isHoveringBar = false;
      if (document.fullscreenElement && !openMenuMode) scheduleFullscreenFade();
    };

    barEl.addEventListener("mouseenter", onEnter);
    barEl.addEventListener("mouseleave", onLeave);
    menuEl.addEventListener("mouseenter", onEnter);
    menuEl.addEventListener("mouseleave", onLeave);

    videoBtn.addEventListener("click", (e) => {
      e.preventDefault();
      e.stopPropagation();
      if (pinnedTarget && pinnedTarget.kind === "audio") {
        toggleQualityMenu("audio");
      } else {
        toggleQualityMenu("video");
      }
    });

    audioBtn.addEventListener("click", (e) => {
      e.preventDefault();
      e.stopPropagation();
      toggleQualityMenu("audio");
    });

    plBtn.addEventListener("click", (e) => {
      e.preventDefault();
      e.stopPropagation();
      closeQualityMenu();
      triggerDownload({ preferAudio: false, asPlaylist: true });
    });

    closeBtn.addEventListener("click", (e) => {
      e.preventDefault();
      e.stopPropagation();
      closeQualityMenu();
      dismissedUntilUrlChange = true;
      barEl.classList.remove("visible");
    });
  }

  function closeQualityMenu() {
    openMenuMode = null;
    if (menuEl) menuEl.classList.remove("open");
    if (barEl) barEl.classList.remove("menu-open");
    if (videoBtn) videoBtn.classList.remove("active");
    if (audioBtn) audioBtn.classList.remove("active");
  }

  function toggleQualityMenu(mode) {
    ensureMiniBar();
    if (openMenuMode === mode) {
      closeQualityMenu();
      return;
    }
    // Refresh latest tab media from background before showing menu
    try {
      chrome.runtime.sendMessage({ type: "getTabMedia" }, (res) => {
        if (res && Array.isArray(res.media) && res.media.length > 0) {
          detectedTabMedia = res.media;
        }
        renderQualityMenu(mode);
      });
    } catch {
      renderQualityMenu(mode);
    }
  }

  function getBadgeForOption(opt) {
    if (opt.height >= 2160) return "4K";
    if (opt.height >= 1440) return "2K";
    if (opt.height >= 1080) return "1080p";
    if (opt.height >= 720) return "720p";
    if (opt.height > 0) return `${opt.height}p`;
    const m = /\b(2160|1440|1080|720|480|360|240)p\b/i.exec(opt.label || opt.url || "");
    if (m) return `${m[1]}p`;
    return (opt.extension || "MP4").toUpperCase();
  }

  function buildAvailableVideoOptions() {
    if (IS_YOUTUBE) {
      return YT_VIDEO_PRESETS.map((p) => ({
        preferredQualityKey: p.key,
        selectedQualityUrl: null,
        badge: p.badge,
        label: p.label
      }));
    }

    const mediaEl = (pinnedTarget && pinnedTarget.mediaEl) || document.querySelector("video, audio");
    const domCandidates = collectDomMediaUrls(mediaEl);
    const combined = [];

    for (const s of detectedTabMedia) {
      if (s.mediaType !== "audio" && !combined.some((c) => c.url === s.url)) combined.push(s);
    }
    for (const d of domCandidates) {
      if (!combined.some((c) => c.url === d.url)) combined.push(d);
    }

    if (combined.length > 0) {
      return combined.map((c) => ({
        preferredQualityKey: c.height ? String(c.height) : null,
        selectedQualityUrl: c.url,
        badge: getBadgeForOption(c),
        label: c.label || `${getBadgeForOption(c)} Video (MP4)`
      }));
    }

    // Fallback when video is handled via yt-dlp
    return YT_VIDEO_PRESETS.map((p) => ({
      preferredQualityKey: p.key,
      selectedQualityUrl: null,
      badge: p.badge,
      label: p.label
    }));
  }

  function renderQualityMenu(mode) {
    ensureMiniBar();
    openMenuMode = mode;
    barEl.classList.add("menu-open");
    videoBtn.classList.toggle("active", mode === "video");
    audioBtn.classList.toggle("active", mode === "audio");

    menuEl.innerHTML = "";

    const hdr = document.createElement("div");
    hdr.className = "idm-menu-hdr";
    hdr.innerHTML = `<span>${mode === "audio" ? "Select Audio Quality" : "Select Video Quality"}</span><span>Nahr</span>`;
    menuEl.appendChild(hdr);

    if (mode === "audio") {
      for (const a of AUDIO_QUALITY_PRESETS) {
        const btn = document.createElement("button");
        btn.className = "idm-menu-item";
        btn.innerHTML = `<span class="idm-badge audio">${a.badge}</span><span class="idm-item-lbl">${a.label}</span>`;
        btn.addEventListener("click", (e) => {
          e.preventDefault();
          e.stopPropagation();
          closeQualityMenu();
          triggerDownload({
            preferAudio: true,
            asPlaylist: false,
            preferredQualityKey: a.key,
            selectedQualityUrl: null,
            chosenLabel: a.label
          });
        });
        menuEl.appendChild(btn);
      }
    } else {
      const options = buildAvailableVideoOptions();
      for (const opt of options) {
        const btn = document.createElement("button");
        btn.className = "idm-menu-item";
        btn.innerHTML = `<span class="idm-badge">${opt.badge}</span><span class="idm-item-lbl">${opt.label}</span>`;
        btn.addEventListener("click", (e) => {
          e.preventDefault();
          e.stopPropagation();
          closeQualityMenu();
          triggerDownload({
            preferAudio: false,
            asPlaylist: false,
            preferredQualityKey: opt.preferredQualityKey,
            selectedQualityUrl: opt.selectedQualityUrl,
            chosenLabel: opt.label
          });
        });
        menuEl.appendChild(btn);
      }
    }

    menuEl.classList.add("open");
    positionMenuBelowBar();
  }

  function positionMenuBelowBar() {
    if (!menuEl || !barEl || !openMenuMode) return;
    const br = barEl.getBoundingClientRect();
    const mw = menuEl.offsetWidth || 245;
    const mh = menuEl.offsetHeight || 160;

    let top = br.bottom + 4;
    if (top + mh > window.innerHeight - 8) {
      top = Math.max(8, br.top - mh - 4);
    }
    let left = br.right - mw;
    if (left < 8) left = 8;
    if (left + mw > window.innerWidth - 8) left = window.innerWidth - mw - 8;

    menuEl.style.top = `${Math.round(top)}px`;
    menuEl.style.left = `${Math.round(left)}px`;
  }

  let toastTimer = null;
  function showToast(msg, isError = false) {
    ensureMiniBar();
    toastEl.textContent = msg;
    toastEl.style.borderColor = isError ? "rgba(239, 68, 68, 0.75)" : "rgba(56, 189, 248, 0.6)";
    toastEl.classList.add("show");
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => toastEl.classList.remove("show"), 2600);
  }

  function scheduleFullscreenFade() {
    clearTimeout(fullscreenIdleTimer);
    fullscreenIdleTimer = setTimeout(() => {
      if (document.fullscreenElement && !isHoveringBar && !openMenuMode && barEl) {
        barEl.classList.remove("visible");
      }
    }, 3000);
  }

  // ------------------------------------------------------------------ Find Active / Visible Video & Audio Players

  function findCandidatePlayers() {
    const results = [];

    for (const el of document.querySelectorAll("video, audio")) {
      const isAudio = el.tagName === "AUDIO";
      const r = el.getBoundingClientRect();
      if (!isAudio && (r.width < 150 || r.height < 85)) continue;
      if (isAudio && (r.width < 120 || r.height < 24)) continue;
      if (isAudio && el.duration && el.duration < 4) continue;

      const isPlaying = !el.paused && !el.ended && el.readyState >= 2;
      const hasMedia = Boolean(el.currentSrc || el.src || el.querySelector("source")) || IS_YOUTUBE || detectedTabMedia.length > 0;
      if (!isPlaying && !hasMedia) continue;

      const box = IS_YOUTUBE ? el.closest("#movie_player, ytd-player, #shorts-player") || el : el;
      results.push({ el: box, mediaEl: el, kind: isAudio ? "audio" : "video", isPlaying });
    }

    if (IS_TOP && results.length === 0) {
      for (const ifr of document.querySelectorAll("iframe")) {
        const r = ifr.getBoundingClientRect();
        if (r.width < 240 || r.height < 140) continue;
        const src = (ifr.src || "").toLowerCase();
        const allow = (ifr.getAttribute("allow") || "").toLowerCase();
        const isPlayerIframe =
          detectedTabMedia.length > 0 ||
          iframePlayingInfo !== null ||
          ifr.hasAttribute("allowfullscreen") ||
          allow.includes("autoplay") ||
          allow.includes("fullscreen") ||
          /embed|player|watch|movie|stream|vid|cinema|cloud/i.test(src);

        if (isPlayerIframe) {
          results.push({
            el: ifr,
            mediaEl: null,
            kind: "iframe-video",
            isPlaying: iframePlayingInfo !== null || detectedTabMedia.length > 0
          });
        }
      }
    }

    results.sort((a, b) => {
      if (a.isPlaying !== b.isPlaying) return a.isPlaying ? -1 : 1;
      const ra = a.el.getBoundingClientRect();
      const rb = b.el.getBoundingClientRect();
      return rb.width * rb.height - ra.width * ra.height;
    });

    return results;
  }

  function getActiveResolutionBadge(targetObj) {
    if (detectedTabMedia.length > 0) {
      const heights = detectedTabMedia.map((m) => m.height).filter((h) => h > 0);
      if (heights.length > 0) {
        const bestH = Math.max(...heights);
        return heights.length > 1 ? `${bestH}p (${heights.length})` : `${bestH}p`;
      }
    }
    if (targetObj && targetObj.mediaEl && targetObj.mediaEl.videoHeight > 0) {
      return `${targetObj.mediaEl.videoHeight}p`;
    }
    return null;
  }

  function positionBarNextToTarget(targetObj) {
    if (!targetObj || dismissedUntilUrlChange) return;
    if (!IS_TOP && !document.fullscreenElement) return;

    const rect = targetObj.el.getBoundingClientRect();
    if (rect.width < 140 || rect.height < 24) {
      if (barEl) barEl.classList.remove("visible");
      closeQualityMenu();
      return;
    }
    if (rect.bottom < 30 || rect.top > window.innerHeight - 20) {
      if (barEl && !isHoveringBar && !openMenuMode) {
        barEl.classList.remove("visible");
        closeQualityMenu();
      }
      return;
    }

    ensureMiniBar();
    pinnedTarget = targetObj;

    const isAudioOnly = targetObj.kind === "audio";
    const resBadge = !isAudioOnly ? getActiveResolutionBadge(targetObj) : null;
    videoLabel.textContent = isAudioOnly
      ? "Download Audio"
      : resBadge
        ? `Video · ${resBadge}`
        : "Download Video";

    audioSep.style.display = isAudioOnly ? "none" : "inline-block";
    audioBtn.style.display = isAudioOnly ? "none" : "inline-flex";

    const showPl = IS_YOUTUBE && hasPlaylistParam();
    plSep.style.display = showPl ? "inline-block" : "none";
    plBtn.style.display = showPl ? "inline-flex" : "none";

    barEl.classList.add("visible");

    const w = barEl.offsetWidth || 175;
    const h = 24;

    let top = rect.top - h - 3;
    if (top < 6) top = Math.max(6, rect.top + 6);
    let left = rect.right - w - 6;
    if (left < 6) left = 6;
    if (left + w > window.innerWidth - 6) left = window.innerWidth - w - 6;

    barEl.style.top = `${Math.round(top)}px`;
    barEl.style.left = `${Math.round(left)}px`;

    if (openMenuMode) positionMenuBelowBar();
  }

  function syncPersistentBar(preferredMediaEl = null) {
    if (location.href !== lastPageHref) {
      lastPageHref = location.href;
      dismissedUntilUrlChange = false;
      closeQualityMenu();
    }
    if (dismissedUntilUrlChange) return;

    const candidates = findCandidatePlayers();
    if (candidates.length === 0) {
      if (barEl && !isHoveringBar && !openMenuMode) {
        barEl.classList.remove("visible");
        closeQualityMenu();
      }
      return;
    }

    let chosen = null;
    if (preferredMediaEl) {
      chosen = candidates.find((c) => c.mediaEl === preferredMediaEl || c.el === preferredMediaEl);
    }
    if (!chosen) {
      chosen = candidates.find((c) => c.isPlaying) || candidates[0];
    }

    if (chosen) {
      positionBarNextToTarget(chosen);
    }
  }

  window.addEventListener("scroll", () => syncPersistentBar(), { passive: true });
  window.addEventListener("resize", () => syncPersistentBar(), { passive: true });
  setInterval(() => {
    if (!document.hidden && !dismissedUntilUrlChange && (!document.fullscreenElement || isHoveringBar || openMenuMode)) {
      syncPersistentBar();
    }
  }, 1200);

  let lastHoverTs = 0;
  window.addEventListener(
    "mousemove",
    (e) => {
      const now = Date.now();
      if (now - lastHoverTs < 100) return;
      lastHoverTs = now;

      if (location.href !== lastPageHref) {
        lastPageHref = location.href;
        dismissedUntilUrlChange = false;
      }
      if (dismissedUntilUrlChange || isHoveringBar || openMenuMode) return;

      if (document.fullscreenElement) {
        syncPersistentBar();
        scheduleFullscreenFade();
        return;
      }

      for (const c of findCandidatePlayers()) {
        const r = c.el.getBoundingClientRect();
        if (
          e.clientX >= r.left - 12 &&
          e.clientX <= r.right + 12 &&
          e.clientY >= r.top - 32 &&
          e.clientY <= r.bottom + 12
        ) {
          positionBarNextToTarget(c);
          return;
        }
      }
    },
    { passive: true }
  );

  if (IS_YOUTUBE) {
    window.addEventListener("yt-navigate-finish", () => {
      dismissedUntilUrlChange = false;
      closeQualityMenu();
      setTimeout(() => syncPersistentBar(), 350);
    });
  }

  // ------------------------------------------------------------------ Trigger Download with Chosen Quality

  function hasPlaylistParam() {
    try {
      return Boolean(new URL(location.href).searchParams.get("list"));
    } catch {
      return false;
    }
  }

  function cleanTitle() {
    let t = (document.title || "")
      .replace(/^\(\d+\)\s*/, "")
      .replace(/\s*[-|–]\s*(YouTube|Watch.*|Free.*)$/i, "")
      .trim();
    if (IS_YOUTUBE) {
      const h1 = document.querySelector("ytd-watch-metadata h1 yt-formatted-string");
      if (h1 && h1.textContent.trim()) t = h1.textContent.trim();
    } else {
      const h1 = document.querySelector("h1");
      if (h1 && h1.textContent.trim() && h1.textContent.trim().length < 120) {
        t = h1.textContent.trim();
      }
    }
    return t || "video";
  }

  function collectDomMediaUrls(mediaEl) {
    if (!mediaEl) return [];
    const list = [];
    const seen = new Set();
    const pushUrl = (u, lbl, h) => {
      if (!u || !u.startsWith("http") || seen.has(u)) return;
      seen.add(u);
      list.push({ label: lbl || "Direct Stream", url: u, extension: null, fileSize: null, height: h || null });
    };

    if (mediaEl.currentSrc && mediaEl.currentSrc.startsWith("http")) {
      const vh = mediaEl.videoHeight || null;
      pushUrl(mediaEl.currentSrc, vh ? `${vh}p (MP4)` : "Default Stream", vh);
    }
    if (mediaEl.src && mediaEl.src.startsWith("http")) {
      const vh = mediaEl.videoHeight || null;
      pushUrl(mediaEl.src, vh ? `${vh}p (MP4)` : "Stream", vh);
    }
    for (const s of mediaEl.querySelectorAll("source")) {
      const su = s.src || s.getAttribute("src");
      const sz = parseInt(s.getAttribute("size") || "0", 10) || null;
      pushUrl(su, s.getAttribute("label") || (sz ? `${sz}p (MP4)` : "Stream"), sz);
    }
    return list;
  }

  function triggerDownload({ preferAudio, asPlaylist, preferredQualityKey, selectedQualityUrl, chosenLabel }) {
    const title = cleanTitle();
    const forceAudio = Boolean(preferAudio) || (pinnedTarget && pinnedTarget.kind === "audio");

    if (IS_YOUTUBE) {
      let targetUrl = location.href;
      let mediaType = "youtube";
      if (asPlaylist || location.pathname.startsWith("/playlist")) {
        mediaType = "playlist";
        try {
          const lid = new URL(location.href).searchParams.get("list");
          if (lid) targetUrl = `https://www.youtube.com/playlist?list=${lid}`;
        } catch {}
      } else {
        try {
          const u = new URL(location.href);
          if (u.pathname.startsWith("/shorts/")) targetUrl = u.origin + u.pathname;
          else if (u.searchParams.get("v")) targetUrl = `https://www.youtube.com/watch?v=${u.searchParams.get("v")}`;
        } catch {}
      }

      sendToBackground(
        {
          url: targetUrl,
          pageUrl: location.href,
          pageTitle: title,
          referrer: location.href,
          mediaType,
          preferAudio: forceAudio,
          preferredQualityKey: preferredQualityKey || null
        },
        chosenLabel
      );
      return;
    }

    const mediaEl = (pinnedTarget && pinnedTarget.mediaEl) || document.querySelector("video, audio");
    const domCandidates = collectDomMediaUrls(mediaEl);
    const iframeSrc =
      pinnedTarget && pinnedTarget.kind === "iframe-video" && pinnedTarget.el && pinnedTarget.el.src
        ? pinnedTarget.el.src
        : null;

    chrome.runtime.sendMessage({ type: "getTabMedia" }, (res) => {
      const sniffed = (res && res.media) || detectedTabMedia || [];
      const playingIframe = (res && res.playingIframe) || iframePlayingInfo;
      const combined = [];

      for (const s of sniffed) {
        if (!combined.some((c) => c.url === s.url)) combined.push(s);
      }
      for (const d of domCandidates) {
        if (!combined.some((c) => c.url === d.url)) combined.push(d);
      }

      if (combined.length > 0) {
        const chosen =
          (selectedQualityUrl && combined.find((c) => c.url === selectedQualityUrl)) ||
          (preferredQualityKey && combined.find((c) => String(c.height) === String(preferredQualityKey))) ||
          combined[0];

        sendToBackground(
          {
            url: chosen.url,
            masterUrl: chosen.masterUrl || null,
            selectedQualityUrl: chosen.url,
            preferredQualityKey: preferredQualityKey || (chosen.height ? String(chosen.height) : null),
            pageUrl: location.href,
            pageTitle: title,
            referrer: chosen.referer || location.href,
            origin: chosen.origin || location.origin,
            mediaType: chosen.mediaType || (forceAudio ? "audio" : "video"),
            preferAudio: forceAudio,
            qualities: combined.map((c) => ({
              label: c.label,
              url: c.url,
              audioUrl: c.audioUrl || null,
              extension: c.extension,
              fileSize: c.fileSize,
              height: c.height || null,
              bandwidth: c.bandwidth || null
            }))
          },
          chosenLabel || chosen.label
        );
        return;
      }

      const fallbackUrl = (playingIframe && playingIframe.iframeUrl) || iframeSrc || location.href;
      sendToBackground(
        {
          url: fallbackUrl,
          pageUrl: location.href,
          pageTitle: title,
          referrer: location.href,
          origin: location.origin,
          mediaType: "ytdlp",
          preferAudio: forceAudio,
          preferredQualityKey: preferredQualityKey || null
        },
        chosenLabel
      );
    });
  }

  function sendToBackground(payload, chosenLabel) {
    chrome.runtime.sendMessage({ type: "downloadWithNahr", payload }, (resp) => {
      if (chrome.runtime.lastError || !resp || !resp.ok) {
        showToast("Open Nahr Download Manager app first!", true);
      } else {
        showToast(chosenLabel ? `Nahr: ${chosenLabel}` : "Opened in Nahr!");
      }
    });
  }

  setTimeout(() => syncPersistentBar(), 500);
})();
