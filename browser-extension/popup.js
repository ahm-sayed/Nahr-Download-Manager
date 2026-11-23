// Nahr Browser Extension — Popup Script (with Direct Per-Quality Selection)

const dot = document.getElementById("statusDot");
const statusText = document.getElementById("statusText");
const toggleBtn = document.getElementById("toggleBtn");
const bypassBtn = document.getElementById("bypassBtn");
const siteModeBtn = document.getElementById("siteModeBtn");
const grabRow = document.getElementById("grabRow");
let currentSiteHost = null;
const grabMediaBtn = document.getElementById("grabMediaBtn");
const grabAudioBtn = document.getElementById("grabAudioBtn");
const qualityList = document.getElementById("qualityList");

let currentTab = null;
let detectedMedia = [];

function isYouTubeUrl(url) {
  return /^https?:\/\/(www\.|m\.|music\.)?(youtube\.com\/(watch|shorts|playlist|embed)|youtu\.be\/)/i.test(url || "");
}

function isHttpTab(url) {
  return /^https?:\/\//i.test(url || "");
}

function getBadgeText(m) {
  if (m.height >= 2160) return "4K";
  if (m.height >= 1440) return "2K";
  if (m.height > 0) return `${m.height}p`;
  return (m.extension || "MP4").toUpperCase();
}

function renderDetectedQualities() {
  qualityList.innerHTML = "";
  if (!detectedMedia || detectedMedia.length === 0) {
    qualityList.style.display = "none";
    return;
  }

  const hdr = document.createElement("div");
  hdr.className = "quality-hdr";
  hdr.textContent = `Detected Qualities (${detectedMedia.length}) — Click to Download:`;
  qualityList.appendChild(hdr);

  for (const item of detectedMedia) {
    const btn = document.createElement("button");
    btn.className = "q-btn";
    btn.innerHTML = `<span class="q-badge">${getBadgeText(item)}</span><span style="flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">${item.label || item.url}</span>`;
    btn.addEventListener("click", () => {
      triggerDownload(false, item.url, item.height ? String(item.height) : null);
    });
    qualityList.appendChild(btn);
  }
  qualityList.style.display = "flex";
}

function updateUI(state) {
  detectedMedia = state.detectedMedia || [];

  if (!state.enabled) {
    dot.className = "status-dot dot-off";
    statusText.textContent = "Integration Paused";
    toggleBtn.textContent = "Enable Nahr Integration";
    toggleBtn.className = "btn btn-primary";
    qualityList.style.display = "none";
  } else if (state.connected) {
    dot.className = "status-dot dot-ok";
    statusText.textContent = "Connected to Nahr";
    toggleBtn.textContent = "Pause Integration";
    toggleBtn.className = "btn btn-secondary";
    renderDetectedQualities();
  } else {
    dot.className = "status-dot dot-ok";
    statusText.textContent = "Ready (auto-starts Nahr)";
    toggleBtn.textContent = "Pause Integration";
    toggleBtn.className = "btn btn-secondary";
    renderDetectedQualities();
  }

  if (state.enabled && currentTab && isHttpTab(currentTab.url)) {
    grabRow.style.display = "flex";
    grabMediaBtn.textContent = "⬇ Video";
    grabAudioBtn.textContent = "♪ Audio (MP3)";
  } else {
    grabRow.style.display = "none";
  }

  if (bypassBtn) {
    bypassBtn.style.display = state.enabled ? "block" : "none";
    if (state.bypassSecondsLeft > 0) {
      bypassBtn.textContent = `Browser Download Active (${state.bypassSecondsLeft}s)`;
    } else {
      bypassBtn.textContent = "Allow Browser Download (30s)";
    }
  }

  if (siteModeBtn) {
    currentSiteHost = state.siteHost || null;
    if (state.enabled && currentSiteHost) {
      siteModeBtn.style.display = "block";
      if (state.isBrowserOnlyHost) {
        siteModeBtn.textContent = `Chrome Direct on ${currentSiteHost} (Switch to Nahr)`;
        siteModeBtn.className = "btn btn-primary";
      } else {
        siteModeBtn.textContent = `Use Chrome Directly on ${currentSiteHost}`;
        siteModeBtn.className = "btn btn-secondary";
      }
    } else {
      siteModeBtn.style.display = "none";
    }
  }
}

chrome.tabs.query({ active: true, currentWindow: true }, (tabs) => {
  currentTab = tabs && tabs[0] ? tabs[0] : null;
  const tabId = currentTab ? currentTab.id : null;
  const tabUrl = currentTab ? currentTab.url : null;

  chrome.runtime.sendMessage({ type: "checkConnection", tabId, tabUrl }, (response) => {
    if (response) updateUI(response);
  });
});

toggleBtn.addEventListener("click", () => {
  chrome.runtime.sendMessage({ type: "toggle" }, (response) => {
    if (response) updateUI(response);
  });
});

if (siteModeBtn) {
  siteModeBtn.addEventListener("click", () => {
    if (!currentSiteHost) return;
    chrome.runtime.sendMessage({ type: "toggleSiteBrowserOnly", host: currentSiteHost }, (response) => {
      if (response && response.ok) {
        if (response.isBrowserOnlyHost) {
          siteModeBtn.textContent = `Chrome Direct on ${currentSiteHost} (Switch to Nahr)`;
          siteModeBtn.className = "btn btn-primary";
        } else {
          siteModeBtn.textContent = `Use Chrome Directly on ${currentSiteHost}`;
          siteModeBtn.className = "btn btn-secondary";
        }
      }
    });
  });
}

if (bypassBtn) {
  bypassBtn.addEventListener("click", () => {
    chrome.runtime.sendMessage({ type: "bypassTemporary", ms: 30000 }, (response) => {
      if (response && response.ok) {
        bypassBtn.textContent = `Browser Download Active (${response.bypassSecondsLeft || 30}s)`;
      }
    });
  });
}

function triggerDownload(preferAudio, selectedQualityUrl = null, preferredQualityKey = null) {
  if (!currentTab) return;
  const yt = isYouTubeUrl(currentTab.url);
  const isPlaylist = yt && /[?&]list=/.test(currentTab.url) && /\/playlist/i.test(currentTab.url);
  const cleanTitle = (currentTab.title || "media").replace(/\s*[-|–]\s*YouTube$/i, "").trim();

  let payload;
  if (yt) {
    payload = {
      url: currentTab.url,
      pageUrl: currentTab.url,
      pageTitle: cleanTitle,
      referrer: currentTab.url,
      mediaType: isPlaylist ? "playlist" : "youtube",
      preferAudio: !!preferAudio,
      preferredQualityKey: preferredQualityKey || null
    };
  } else if (detectedMedia.length > 0) {
    const chosen =
      (selectedQualityUrl && detectedMedia.find((x) => x.url === selectedQualityUrl)) || detectedMedia[0];
    payload = {
      url: chosen.url,
      masterUrl: chosen.masterUrl || null,
      selectedQualityUrl: chosen.url,
      preferredQualityKey: preferredQualityKey || (chosen.height ? String(chosen.height) : null),
      pageUrl: currentTab.url,
      pageTitle: cleanTitle,
      referrer: chosen.referer || currentTab.url,
      origin: chosen.origin || null,
      mediaType: chosen.mediaType || "video",
      preferAudio: !!preferAudio,
      qualities: detectedMedia
    };
  } else {
    payload = {
      url: currentTab.url,
      pageUrl: currentTab.url,
      pageTitle: cleanTitle,
      referrer: currentTab.url,
      mediaType: "ytdlp",
      preferAudio: !!preferAudio,
      preferredQualityKey: preferredQualityKey || null
    };
  }

  chrome.runtime.sendMessage({ type: "downloadWithNahr", tabId: currentTab.id, payload }, () => {
    window.close();
  });
}

grabMediaBtn.addEventListener("click", () => triggerDownload(false));
grabAudioBtn.addEventListener("click", () => triggerDownload(true));
