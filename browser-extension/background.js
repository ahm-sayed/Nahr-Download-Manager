// Nahr Download Manager — Browser Extension Background Service Worker (v1.4)
// Captures HLS (.m3u8 / disguised #EXTM3U) master & variant streams across all nested iframes,
// resolves multi-quality lists (4K / 1080p / 720p / 480p / 360p), preserves Referer & Origin headers
// for streaming CDNs (e.g. cinejoy.pk / lightspeed.st), and forwards user-selected quality to Nahr.

const NAHR_API = "http://127.0.0.1:36775";
const NATIVE_HOST_NAME = "com.nahr.downloader";
let nahrEnabled = true;
let nahrConnected = false;
let bypassUntil = 0;
let launchingPromise = null;
let nativeLaunchCooldownUntil = 0;

// Map<tabId, Array<{url, masterUrl, audioUrl, label, extension, fileSize, height, bandwidth, mediaType, isMaster, referer, origin}>>
const tabMedia = new Map();
// Map<tabId, {iframeUrl: string, timestamp: number}> when a <video> inside a subframe starts playing
const tabPlayingIframe = new Map();
// Temporary cache of request headers by requestId: Map<requestId, {referer, origin}>
const reqHeadersCache = new Map();

const EXCLUDED_URL_PATTERNS = [
  /youtube\.com\/s\/search\/audio\//i,
  /googlevideo\.com\/videoplayback/i, // YouTube handled via clean watch URL + yt-dlp
  /gstatic\.com\/.*\/sounds\//i,
  /web\.whatsapp\.com\/res\/.*\.mp3/i,
  /doubleclick\.net|googlesyndication\.com/i,
  /\.(ts|m4s|aac|vtt|srt)(\?|$)/i // skip individual 2-sec HLS fragments
];

const RES_TOKEN_RX = /(?<=[\/_\-\.\?=])(2160|1440|1080|720|576|540|480|360|240)(p)?(?=[\/_\-\.\?&]|$)/gi;

function extractHeightFromUrl(str) {
  if (!str || typeof str !== "string") return null;
  const matches = [...str.matchAll(RES_TOKEN_RX)];
  if (matches.length === 0) return null;
  for (const m of matches) {
    if (m[2]) return parseInt(m[1], 10);
  }
  return parseInt(matches[matches.length - 1][1], 10);
}

function formatResHead(height) {
  if (!height || height <= 0) return null;
  const badge =
    height >= 2160 ? " 4K" : height >= 1440 ? " 2K" : height === 1080 ? " Full HD" : height === 720 ? " HD" : "";
  return `${height}p${badge}`;
}

chrome.storage.local.get(["nahrEnabled"], (res) => {
  if (typeof res.nahrEnabled === "boolean") {
    nahrEnabled = res.nahrEnabled;
    updateBadge();
  }
});

// ------------------------------------------------------------------ connection check & Native Messaging auto-launch

async function checkConnection() {
  try {
    const extId = chrome.runtime?.id ? `?extId=${encodeURIComponent(chrome.runtime.id)}` : "";
    const resp = await fetch(`${NAHR_API}/ping${extId}`, { method: "GET" });
    if (resp.ok) {
      const data = await resp.json();
      nahrConnected = data.status === "ok";
    } else {
      nahrConnected = false;
    }
  } catch {
    nahrConnected = false;
  }
  updateBadge();
  return nahrConnected;
}

function sendNativeHostMessage(payload) {
  return new Promise((resolve) => {
    if (!chrome.runtime?.sendNativeMessage) {
      resolve({ ok: false, error: "nativeMessaging unavailable" });
      return;
    }
    try {
      chrome.runtime.sendNativeMessage(NATIVE_HOST_NAME, payload, (response) => {
        if (chrome.runtime.lastError) {
          resolve({ ok: false, error: chrome.runtime.lastError.message || "native host error" });
          return;
        }
        resolve(response || { ok: false });
      });
    } catch (err) {
      resolve({ ok: false, error: err?.message || "nativeMessaging error" });
    }
  });
}

async function ensureNahrRunning() {
  if (await checkConnection()) {
    nativeLaunchCooldownUntil = 0;
    return true;
  }
  if (Date.now() < nativeLaunchCooldownUntil) return false;
  if (launchingPromise) return await launchingPromise;

  launchingPromise = (async () => {
    const res = await sendNativeHostMessage({ action: "ensureRunning" });
    if (res && res.ok) {
      nativeLaunchCooldownUntil = 0;
      return await checkConnection();
    }
    // Even if native host timed out early while Nahr was finishing startup, probe briefly
    for (let i = 0; i < 6; i++) {
      await new Promise((r) => setTimeout(r, 250));
      if (await checkConnection()) {
        nativeLaunchCooldownUntil = 0;
        return true;
      }
    }
    // Back off for 30s if Native Messaging failed (e.g. blocked by OS policy or app uninstalled)
    nativeLaunchCooldownUntil = Date.now() + 30000;
    return false;
  })();

  try {
    return await launchingPromise;
  } finally {
    launchingPromise = null;
  }
}

function updateBadge(tabId) {
  if (!nahrEnabled) {
    chrome.action.setBadgeText({ text: "OFF" });
    chrome.action.setBadgeBackgroundColor({ color: "#64748b" });
    return;
  }
  if (tabId != null) {
    const count = (tabMedia.get(tabId) || []).length;
    chrome.action.setBadgeText({ tabId, text: count > 0 ? String(count) : "" });
    if (count > 0) chrome.action.setBadgeBackgroundColor({ tabId, color: "#2563eb" });
  } else {
    chrome.action.setBadgeText({ text: "" });
  }
}

checkConnection();
setInterval(checkConnection, 20000);

// ------------------------------------------------------------------ send to Nahr desktop app

function getCookiesHeader(url) {
  return new Promise((resolve) => {
    if (!url || !chrome.cookies?.getAll) {
      resolve(null);
      return;
    }
    try {
      chrome.cookies.getAll({ url }, (cookies) => {
        if (chrome.runtime.lastError || !cookies || cookies.length === 0) {
          resolve(null);
          return;
        }
        resolve(cookies.map((c) => `${c.name}=${c.value}`).join("; "));
      });
    } catch {
      resolve(null);
    }
  });
}

async function postPromptToNahr(payload) {
  const resp = await fetch(`${NAHR_API}/prompt`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(payload)
  });
  if (!resp.ok) throw new Error(`Nahr returned HTTP ${resp.status}`);
  nahrConnected = true;
  updateBadge();
  return await resp.json();
}

async function sendToNahr(payload) {
  const enriched = { ...payload };
  if (!enriched.cookies && enriched.url && /^https?:\/\//i.test(enriched.url)) {
    const cookieHeader = await getCookiesHeader(enriched.url);
    if (cookieHeader) enriched.cookies = cookieHeader;
  }

  try {
    return await postPromptToNahr(enriched);
  } catch (firstErr) {
    // If Nahr is closed, auto-launch it silently via Native Messaging (IDM-style) and retry immediately!
    const started = await ensureNahrRunning();
    if (!started) throw firstErr;
    return await postPromptToNahr(enriched);
  }
}

// ------------------------------------------------------------------ register detected stream(s) for a tab

function sortAndBroadcastTabMedia(tabId, list) {
  // Sort: known resolutions descending (2160 -> 1080 -> 720 -> 480 -> 360), then master playlists, then others
  list.sort((a, b) => {
    const ha = a.height || 0;
    const hb = b.height || 0;
    if (ha !== hb) return hb - ha;
    if (Boolean(a.isMaster) !== Boolean(b.isMaster)) return a.isMaster ? -1 : 1;
    return (b.bandwidth || 0) - (a.bandwidth || 0);
  });

  if (list.length > 20) list.length = 20;
  updateBadge(tabId);
  chrome.tabs.sendMessage(tabId, { type: "nahrMediaUpdated", media: list }).catch(() => {});
}

function upsertSingleStream(list, entry) {
  if (!entry || !entry.url) return;
  if (!entry.height) {
    entry.height = extractHeightFromUrl(entry.url) || extractHeightFromUrl(entry.label) || null;
  }
  if (entry.height && (!entry.label || entry.label.startsWith("HLS "))) {
    entry.label = `${formatResHead(entry.height)} (${(entry.extension || "mp4").toUpperCase()})`;
  }

  // Check if same URL or same HLS resolution height already exists
  const byUrlIdx = list.findIndex((x) => x.url === entry.url);
  if (byUrlIdx >= 0) {
    const prev = list[byUrlIdx];
    list[byUrlIdx] = {
      ...prev,
      ...entry,
      label: entry.label && !entry.label.startsWith("HLS ") ? entry.label : prev.label || entry.label,
      height: entry.height || prev.height || null,
      bandwidth: entry.bandwidth || prev.bandwidth || null,
      masterUrl: entry.masterUrl || prev.masterUrl || null,
      referer: entry.referer || prev.referer || null,
      origin: entry.origin || prev.origin || null
    };
    return;
  }

  if (entry.height && entry.mediaType === "hls") {
    const byHeightIdx = list.findIndex((x) => x.mediaType === "hls" && x.height === entry.height);
    if (byHeightIdx >= 0) {
      const prev = list[byHeightIdx];
      // Prefer the entry with bandwidth info or masterUrl
      if ((entry.bandwidth || 0) >= (prev.bandwidth || 0)) {
        list[byHeightIdx] = {
          ...prev,
          ...entry,
          masterUrl: entry.masterUrl || prev.masterUrl || null,
          referer: entry.referer || prev.referer || null,
          origin: entry.origin || prev.origin || null
        };
      }
      return;
    }
  }

  list.push(entry);
}

function registerTabStream(tabId, entry) {
  if (tabId == null || tabId < 0 || !entry) return;
  let list = tabMedia.get(tabId);
  if (!list) {
    list = [];
    tabMedia.set(tabId, list);
  }

  // If page-hook parsed a Master Playlist into multiple resolution variants, register all of them!
  if (Array.isArray(entry.variants) && entry.variants.length > 0) {
    const masterUrl = entry.masterUrl || entry.url;
    // Remove any generic un-parsed placeholder for the master URL itself
    for (let i = list.length - 1; i >= 0; i--) {
      if (list[i].url === masterUrl || (!list[i].height && list[i].mediaType === "hls")) {
        list.splice(i, 1);
      }
    }
    for (const v of entry.variants) {
      upsertSingleStream(list, {
        url: v.url,
        masterUrl,
        audioUrl: v.audioUrl || null,
        label: v.label,
        extension: v.extension || "mp4",
        fileSize: null,
        height: v.height || extractHeightFromUrl(v.url) || null,
        bandwidth: v.bandwidth || null,
        mediaType: "hls",
        isMaster: false,
        referer: entry.referer || null,
        origin: entry.origin || null
      });
    }
    sortAndBroadcastTabMedia(tabId, list);
    return;
  }

  // If we already have resolved multi-height HLS variants in this tab, ignore generic height-less master placeholders
  const hasResolvedHeights = list.some((x) => x.mediaType === "hls" && x.height > 0);
  const entryHeight = entry.height || extractHeightFromUrl(entry.url);
  if (hasResolvedHeights && entry.mediaType === "hls" && !entryHeight && entry.isMaster) {
    // Store masterUrl on existing variants so C# fallback knows it
    for (const item of list) {
      if (item.mediaType === "hls" && !item.masterUrl) item.masterUrl = entry.url;
    }
    return;
  }

  upsertSingleStream(list, entry);
  sortAndBroadcastTabMedia(tabId, list);
}

// ------------------------------------------------------------------ webRequest header & stream capture

chrome.webRequest.onBeforeSendHeaders.addListener(
  (details) => {
    if (details.tabId < 0) return;
    let referer = null;
    let origin = null;
    for (const h of details.requestHeaders || []) {
      const n = h.name.toLowerCase();
      if (n === "referer") referer = h.value;
      else if (n === "origin") origin = h.value;
    }
    if (referer || origin) {
      reqHeadersCache.set(details.requestId, { referer, origin });
      if (reqHeadersCache.size > 800) {
        const firstKey = reqHeadersCache.keys().next().value;
        reqHeadersCache.delete(firstKey);
      }
    }
  },
  { urls: ["<all_urls>"] },
  ["requestHeaders", "extraHeaders"]
);

function formatBytes(bytes) {
  if (!bytes || bytes <= 0) return "";
  const mb = bytes / (1024 * 1024);
  return mb >= 1024 ? `${(mb / 1024).toFixed(1)} GB` : mb >= 1 ? `${mb.toFixed(1)} MB` : `${Math.round(bytes / 1024)} KB`;
}

function guessExtFromMime(mime, url) {
  if (mime) {
    const m = mime.toLowerCase();
    if (m.includes("mpegurl")) return "mp4";
    if (m.includes("mp4")) return "mp4";
    if (m.includes("webm")) return "webm";
    if (m.includes("mpeg") || m.includes("mp3")) return "mp3";
    if (m.includes("mp4a") || m.includes("aac") || m.includes("x-m4a")) return "m4a";
    if (m.includes("ogg")) return "ogg";
    if (m.includes("wav")) return "wav";
    if (m.includes("matroska") || m.includes("mkv")) return "mkv";
  }
  try {
    const p = new URL(url).pathname;
    const dot = p.lastIndexOf(".");
    if (dot > -1 && p.length - dot <= 6) return p.slice(dot + 1).toLowerCase();
  } catch {}
  return null;
}

chrome.webRequest.onHeadersReceived.addListener(
  (details) => {
    const cachedHdrs = reqHeadersCache.get(details.requestId) || {};
    reqHeadersCache.delete(details.requestId);

    if (details.tabId < 0 || !details.url.startsWith("http")) return;
    if (EXCLUDED_URL_PATTERNS.some((rx) => rx.test(details.url))) return;

    let contentType = "";
    let contentLength = 0;
    for (const h of details.responseHeaders || []) {
      const name = h.name.toLowerCase();
      if (name === "content-type") contentType = (h.value || "").toLowerCase();
      else if (name === "content-length") contentLength = parseInt(h.value || "0", 10) || 0;
      else if (name === "content-range" && h.value) {
        const m = /\/(\d+)$/.exec(h.value);
        if (m) contentLength = parseInt(m[1], 10) || contentLength;
      }
    }

    const isHls =
      /\.m3u8(\?|#|$)/i.test(details.url) ||
      contentType.includes("mpegurl") ||
      contentType.includes("apple.mpegurl");

    if (isHls) {
      const urlHeight = extractHeightFromUrl(details.url);
      // A URL with a resolution folder/token (e.g. "/1080p/playlist.m3u8") is a resolution variant, NOT a master playlist!
      const isMaster = !urlHeight && /master/i.test(details.url);
      const label = urlHeight
        ? `${formatResHead(urlHeight)} (MP4)`
        : isMaster
          ? "HLS Master Stream (Multi-Quality)"
          : "HLS Video Stream (MP4)";

      registerTabStream(details.tabId, {
        url: details.url,
        masterUrl: isMaster ? details.url : null,
        label,
        extension: "mp4",
        fileSize: null,
        height: urlHeight || null,
        mediaType: "hls",
        isMaster,
        referer: cachedHdrs.referer || details.initiator || null,
        origin: cachedHdrs.origin || details.initiator || null
      });
      return;
    }

    const isVideo = contentType.startsWith("video/") && !contentType.includes("mp2t");
    const isAudio = contentType.startsWith("audio/") && !contentType.includes("mpegurl");
    if (!isVideo && !isAudio) return;

    // Skip tiny UI notification clips (< 200 KB)
    if (contentLength > 0 && contentLength < 200 * 1024) return;

    const ext = guessExtFromMime(contentType, details.url) || (isAudio ? "mp3" : "mp4");
    const sizeLabel = formatBytes(contentLength);
    const urlHeight = isVideo ? extractHeightFromUrl(details.url) : null;
    const kindLabel = isAudio ? "Audio" : urlHeight ? formatResHead(urlHeight) : "Video";

    registerTabStream(details.tabId, {
      url: details.url,
      label: `${kindLabel} (${ext.toUpperCase()}${sizeLabel ? " · " + sizeLabel : ""})`,
      extension: ext,
      fileSize: contentLength > 0 ? contentLength : null,
      height: urlHeight || null,
      mediaType: isAudio ? "audio" : "video",
      isMaster: false,
      referer: cachedHdrs.referer || details.initiator || null,
      origin: cachedHdrs.origin || details.initiator || null
    });
  },
  { urls: ["<all_urls>"] },
  ["responseHeaders"]
);

chrome.tabs.onRemoved.addListener((tabId) => {
  tabMedia.delete(tabId);
  tabPlayingIframe.delete(tabId);
});
chrome.tabs.onUpdated.addListener((tabId, changeInfo) => {
  if (changeInfo.status === "loading" && changeInfo.url) {
    tabMedia.delete(tabId);
    tabPlayingIframe.delete(tabId);
  }
});

// ------------------------------------------------------------------ download interception

chrome.downloads.onCreated.addListener(async (downloadItem) => {
  if (!nahrEnabled) return;
  if (Date.now() < bypassUntil) return;

  const url = downloadItem.finalUrl || downloadItem.url;
  if (!url || url.startsWith("data:") || url.startsWith("blob:") || url.startsWith("chrome")) return;
  if (EXCLUDED_URL_PATTERNS.some((rx) => rx.test(url))) return;

  const alive = nahrConnected || (await ensureNahrRunning());
  if (!alive) return;

  try {
    chrome.downloads.cancel(downloadItem.id);
    chrome.downloads.erase({ id: downloadItem.id });
  } catch {}

  try {
    await sendToNahr({
      url,
      filename: downloadItem.filename ? downloadItem.filename.split(/[/\\]/).pop() : null,
      referrer: downloadItem.referrer || null,
      userAgent: navigator.userAgent,
      mimeType: downloadItem.mime || null,
      fileSize: downloadItem.totalBytes > 0 ? downloadItem.totalBytes : null,
      mediaType: "file"
    });
  } catch (err) {
    console.error("[Nahr] Failed to send download:", err);
  }
});

// ------------------------------------------------------------------ message handling

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (msg.type === "iframeVideoPlaying") {
    const tabId = sender.tab?.id;
    if (tabId != null) {
      const info = { iframeUrl: msg.iframeUrl || sender.url || "", timestamp: Date.now() };
      tabPlayingIframe.set(tabId, info);
      chrome.tabs
        .sendMessage(tabId, {
          type: "nahrIframeVideoPlaying",
          iframeUrl: info.iframeUrl,
          media: tabMedia.get(tabId) || []
        })
        .catch(() => {});
    }
    sendResponse({ ok: true });
    return true;
  }

  if (msg.type === "streamDetectedFromHook") {
    const tabId = sender.tab?.id;
    if (tabId != null && msg.stream) {
      registerTabStream(tabId, {
        url: msg.stream.url,
        masterUrl: msg.stream.masterUrl || (msg.stream.isMaster ? msg.stream.url : null),
        label:
          msg.stream.label ||
          (msg.stream.isMaster ? "HLS Master Stream (Multi-Quality MP4)" : "HLS Stream (MP4)"),
        extension: "mp4",
        fileSize: null,
        height: msg.stream.height || extractHeightFromUrl(msg.stream.url) || null,
        mediaType: "hls",
        isMaster: Boolean(msg.stream.isMaster),
        variants: Array.isArray(msg.stream.variants) ? msg.stream.variants : null,
        referer: msg.stream.referer || sender.url || null,
        origin: msg.stream.origin || null
      });
    }
    sendResponse({ ok: true });
    return true;
  }

  if (msg.type === "getStatus") {
    const tabId = msg.tabId ?? sender.tab?.id;
    const detected = tabId != null ? tabMedia.get(tabId) || [] : [];
    sendResponse({ enabled: nahrEnabled, connected: nahrConnected, detectedMedia: detected });
    return true;
  }

  if (msg.type === "toggle") {
    nahrEnabled = !nahrEnabled;
    chrome.storage.local.set({ nahrEnabled });
    updateBadge();
    sendResponse({ enabled: nahrEnabled, connected: nahrConnected });
    return true;
  }

  if (msg.type === "checkConnection") {
    checkConnection().then(() => {
      const tabId = msg.tabId ?? sender.tab?.id;
      const detected = tabId != null ? tabMedia.get(tabId) || [] : [];
      const bypassSecondsLeft = Math.max(0, Math.ceil((bypassUntil - Date.now()) / 1000));
      sendResponse({ enabled: nahrEnabled, connected: nahrConnected, detectedMedia: detected, bypassSecondsLeft });
    });
    return true;
  }

  if (msg.type === "altBypass") {
    bypassUntil = Date.now() + 4000;
    sendResponse({ ok: true });
    return true;
  }

  if (msg.type === "bypassTemporary") {
    const ms = typeof msg.ms === "number" && msg.ms > 0 ? Math.min(msg.ms, 300000) : 30000;
    bypassUntil = Date.now() + ms;
    sendResponse({ ok: true, bypassSecondsLeft: Math.ceil(ms / 1000) });
    return true;
  }

  if (msg.type === "getTabMedia") {
    const tabId = sender.tab?.id ?? msg.tabId;
    sendResponse({
      media: tabId != null ? tabMedia.get(tabId) || [] : [],
      playingIframe: tabId != null ? tabPlayingIframe.get(tabId) || null : null
    });
    return true;
  }

  if (msg.type === "downloadWithNahr") {
    const tabId = sender.tab?.id ?? msg.tabId;
    const sniffed = tabId != null ? tabMedia.get(tabId) || [] : [];
    const playingIframe = tabId != null ? tabPlayingIframe.get(tabId) : null;
    const payload = { ...msg.payload, userAgent: navigator.userAgent };

    if (payload.mediaType !== "youtube" && payload.mediaType !== "playlist") {
      if (sniffed.length > 0) {
        const chosenSniffed =
          (payload.selectedQualityUrl && sniffed.find((s) => s.url === payload.selectedQualityUrl)) ||
          (payload.preferredQualityKey &&
            sniffed.find((s) => String(s.height) === String(payload.preferredQualityKey))) ||
          sniffed[0];

        if (!payload.url || !payload.url.startsWith("http") || chosenSniffed.mediaType === "hls") {
          payload.url = chosenSniffed.url;
          payload.masterUrl = chosenSniffed.masterUrl || payload.masterUrl || null;
          payload.mediaType = chosenSniffed.mediaType;
          payload.referrer = chosenSniffed.referer || payload.referrer;
          payload.origin = chosenSniffed.origin || payload.origin;
        }

        if (!payload.qualities || payload.qualities.length === 0) {
          payload.qualities = sniffed.map((s) => ({
            label: s.label,
            url: s.url,
            audioUrl: s.audioUrl || null,
            extension: s.extension,
            fileSize: s.fileSize,
            height: s.height || null,
            bandwidth: s.bandwidth || null
          }));
        }
      } else if (!payload.url || !payload.url.startsWith("http")) {
        payload.url = (playingIframe && playingIframe.iframeUrl) || payload.pageUrl || sender.tab?.url || "";
        payload.mediaType = "ytdlp";
      }
    }

    const finalizeAndSend = (topTitle) => {
      if (topTitle && (!payload.pageTitle || payload.pageTitle === "media" || payload.pageTitle.length < 3)) {
        payload.pageTitle = topTitle;
      }
      sendToNahr(payload)
        .then(() => sendResponse({ ok: true }))
        .catch((err) => sendResponse({ ok: false, error: err.message || "Nahr is not running" }));
    };

    if (sender.tab && sender.tab.title) {
      finalizeAndSend(sender.tab.title.replace(/\s*[-|–]\s*(Watch|Free|Online|YouTube).*$/i, "").trim());
    } else {
      finalizeAndSend(null);
    }
    return true;
  }
});
