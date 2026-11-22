// Nahr Download Manager — MAIN-World HLS (#EXTM3U) Master/Variant Parser & Multi-Quality Detector
// Runs at document_start inside pages & embedded player iframes (e.g. cinejoy.pk / lightspeed.st / vidsrc / hls.js / jwplayer).
// - Parses #EXT-X-STREAM-INF master playlists into individual resolution choices (4K, 1080p, 720p, 480p, 360p).
// - Extracts resolution from URL paths (e.g. ".../2/1080p/playlist.m3u8" -> 1080p Full HD) and probes sibling resolutions.
// - Buffers all detected streams from document_start and replays them when content.js initializes.

(() => {
  if (window.__nahrMainWorldHooked) return;
  if (location.protocol !== "http:" && location.protocol !== "https:") return;
  if (/(^|\.)(challenges\.cloudflare\.com|cloudflarecaptcha\.com|hcaptcha\.com|recaptcha\.net)$/i.test(location.hostname)) {
    return;
  }
  window.__nahrMainWorldHooked = true;

  const origFetch = typeof window.fetch === "function" ? window.fetch.bind(window) : null;
  const reportedUrls = new Set();
  const probedSiblingBases = new Set();
  const history = [];

  const RES_TOKEN_RX = /(?<=[\/_\-\.\?=])(2160|1440|1080|720|576|540|480|360|240)(p)?(?=[\/_\-\.\?&]|$)/gi;
  const ATTR_RX = /([A-Z0-9\-]+)=(?:"([^"]*)"|([^,\s]*))/gi;
  const STANDARD_HEIGHTS = [2160, 1440, 1080, 720, 480, 360];

  function extractHeightFromText(str) {
    if (!str || typeof str !== "string") return null;
    const matches = [...str.matchAll(RES_TOKEN_RX)];
    if (matches.length === 0) return null;
    for (const m of matches) {
      if (m[2]) return parseInt(m[1], 10);
    }
    return parseInt(matches[matches.length - 1][1], 10);
  }

  function formatResHead(height) {
    if (!height || height <= 0) return "Stream";
    const badge =
      height >= 2160 ? " 4K" : height >= 1440 ? " 2K" : height === 1080 ? " Full HD" : height === 720 ? " HD" : "";
    return `${height}p${badge}`;
  }

  function buildVariantLabel(height, bandwidth, name, durSec) {
    const head = height > 0 ? formatResHead(height) : name || "Video Stream";
    const parts = ["MP4"];
    if (bandwidth && bandwidth > 0) {
      parts.push(`${(bandwidth / 1000000).toFixed(1)} Mbps`);
    }
    if (durSec && durSec > 60) {
      parts.push(`${Math.round(durSec / 60)} min`);
    }
    return `${head} (${parts.join(" · ")})`;
  }

  function parseAttrs(raw) {
    const map = {};
    if (!raw) return map;
    for (const m of raw.matchAll(ATTR_RX)) {
      map[m[1].toUpperCase()] = m[2] !== undefined ? m[2] : m[3] || "";
    }
    return map;
  }

  function estimateDurationSec(text) {
    let sum = 0;
    for (const line of text.split(/\r?\n/)) {
      if (line.startsWith("#EXTINF:")) {
        const d = parseFloat(line.slice(8).split(",")[0]);
        if (!isNaN(d) && d > 0) sum += d;
      }
    }
    return sum;
  }

  function parseMasterPlaylist(text, masterAbsUrl) {
    const lines = text
      .split(/\r?\n/)
      .map((l) => l.trim())
      .filter(Boolean);

    const audioGroups = {};
    for (const line of lines) {
      if (!line.toUpperCase().startsWith("#EXT-X-MEDIA:")) continue;
      const attrs = parseAttrs(line.slice(13));
      if ((attrs["TYPE"] || "").toUpperCase() === "AUDIO" && attrs["GROUP-ID"] && attrs["URI"]) {
        try {
          const resolvedAudio = new URL(attrs["URI"], masterAbsUrl).href;
          if (!audioGroups[attrs["GROUP-ID"]] || (attrs["DEFAULT"] || "").toUpperCase() === "YES") {
            audioGroups[attrs["GROUP-ID"]] = resolvedAudio;
          }
        } catch {}
      }
    }

    const rawVariants = [];
    for (let i = 0; i < lines.length - 1; i++) {
      const line = lines[i];
      if (!line.toUpperCase().startsWith("#EXT-X-STREAM-INF:")) continue;
      const attrs = parseAttrs(line.slice(18));

      let width = null;
      let height = null;
      if (attrs["RESOLUTION"]) {
        const parts = attrs["RESOLUTION"].split(/[xX]/);
        if (parts.length === 2) {
          width = parseInt(parts[0], 10) || null;
          height = parseInt(parts[1], 10) || null;
        }
      }
      const bandwidth = parseInt(attrs["BANDWIDTH"] || "0", 10) || null;
      const audioUrl = attrs["AUDIO"] && audioGroups[attrs["AUDIO"]] ? audioGroups[attrs["AUDIO"]] : null;

      let nextUri = null;
      for (let j = i + 1; j < lines.length; j++) {
        if (!lines[j].startsWith("#")) {
          nextUri = lines[j];
          i = j;
          break;
        }
      }
      if (!nextUri) continue;

      try {
        const resolvedUrl = new URL(nextUri, masterAbsUrl).href;
        if (!height) {
          height = extractHeightFromText(attrs["NAME"]) || extractHeightFromText(resolvedUrl);
        }
        const label = buildVariantLabel(height, bandwidth, attrs["NAME"], 0);
        rawVariants.push({
          label,
          url: resolvedUrl,
          audioUrl,
          extension: "mp4",
          height: height || null,
          width: width || null,
          bandwidth: bandwidth || null,
          mediaType: "hls",
          isMaster: false
        });
      } catch {}
    }

    // Deduplicate by height (keep highest bandwidth per height) and sort highest resolution first
    const byKey = new Map();
    for (const v of rawVariants) {
      const key = v.height ? `h:${v.height}` : `u:${v.url}`;
      const existing = byKey.get(key);
      if (!existing || (v.bandwidth || 0) > (existing.bandwidth || 0)) {
        byKey.set(key, v);
      }
    }

    return [...byKey.values()].sort((a, b) => (b.height || 0) - (a.height || 0) || (b.bandwidth || 0) - (a.bandwidth || 0));
  }

  function broadcastPayload(payload) {
    history.push(payload);
    if (history.length > 40) history.shift();
    try {
      window.postMessage(payload, "*");
    } catch {}
  }

  // Replay buffered streams whenever content.js asks for them
  window.addEventListener("message", (e) => {
    if (e.data && e.data.__nahrRequestStreams) {
      for (const item of history) {
        try {
          window.postMessage(item, "*");
        } catch {}
      }
    }
  });

  async function tryProbeSiblingsFromMainWorld(singleAbsUrl, selfHeight, durSec) {
    if (!origFetch || !selfHeight) return;
    const matches = [...singleAbsUrl.matchAll(RES_TOKEN_RX)];
    if (matches.length === 0) return;

    let tokenMatch = matches[matches.length - 1];
    for (const m of matches) {
      if (m[2]) tokenMatch = m;
    }

    const tokenIdx = tokenMatch.index;
    const tokenStr = tokenMatch[0];
    const hasPSuffix = Boolean(tokenMatch[2]);
    const baseKey = singleAbsUrl.slice(0, tokenIdx);
    if (probedSiblingBases.has(baseKey)) return;
    probedSiblingBases.add(baseKey);

    // 1. Check parent master.m3u8 / playlist.m3u8
    const lastSlash = baseKey.lastIndexOf("/");
    if (lastSlash > 8) {
      const parentDir = baseKey.slice(0, lastSlash + 1);
      for (const masterName of ["master.m3u8", "playlist.m3u8", "index.m3u8"]) {
        const candidateMaster = parentDir + masterName;
        if (candidateMaster === singleAbsUrl) continue;
        try {
          const resp = await origFetch(candidateMaster, { method: "GET", credentials: "omit" });
          if (resp && resp.ok) {
            const txt = await resp.text();
            if (txt && txt.includes("#EXT-X-STREAM-INF")) {
              notifyStream(resp.url || candidateMaster, txt);
              return;
            }
          }
        } catch {}
      }
    }

    // 2. Probe sibling resolution paths in parallel (e.g. /1080p/ -> /720p/, /480p/, /360p/)
    await Promise.all(
      STANDARD_HEIGHTS.filter((h) => h !== selfHeight).map(async (h) => {
        const rep = hasPSuffix ? `${h}p` : `${h}`;
        const siblingUrl = singleAbsUrl.slice(0, tokenIdx) + rep + singleAbsUrl.slice(tokenIdx + tokenStr.length);
        if (reportedUrls.has(siblingUrl)) return;
        try {
          const resp = await origFetch(siblingUrl, { method: "GET" });
          if (resp && resp.ok) {
            const txt = (await resp.text()).slice(0, 4096);
            if (txt.includes("#EXTM3U") && txt.includes("#EXTINF")) {
              reportedUrls.add(siblingUrl);
              broadcastPayload({
                __nahrStreamDetected: true,
                url: resp.url || siblingUrl,
                label: buildVariantLabel(h, null, null, durSec),
                height: h,
                mediaType: "hls",
                isMaster: false,
                origin: location.origin,
                referer: location.href
              });
            }
          }
        } catch {}
      })
    );
  }

  function notifyStream(rawUrl, textSample) {
    try {
      if (!rawUrl) return;
      const absUrl = new URL(rawUrl, location.href).href;
      if (!absUrl.startsWith("http")) return;
      if (/googlevideo\.com\/videoplayback/i.test(absUrl)) return;

      const isHlsBody = typeof textSample === "string" && textSample.includes("#EXTM3U");
      const isHlsUrl = /\.m3u8(\?|#|$)/i.test(absUrl);
      if (!isHlsBody && !isHlsUrl) return;

      const isMaster = typeof textSample === "string" && textSample.includes("#EXT-X-STREAM-INF");
      if (isMaster) {
        const variants = parseMasterPlaylist(textSample, absUrl);
        reportedUrls.add(absUrl);
        for (const v of variants) reportedUrls.add(v.url);

        // Mark sibling bases as already covered by this master playlist
        for (const v of variants) {
          const ms = [...v.url.matchAll(RES_TOKEN_RX)];
          if (ms.length > 0) probedSiblingBases.add(v.url.slice(0, ms[ms.length - 1].index));
        }

        broadcastPayload({
          __nahrStreamDetected: true,
          url: variants.length > 0 ? variants[0].url : absUrl,
          masterUrl: absUrl,
          label: variants.length > 0 ? variants[0].label : "HLS Master Stream (MP4)",
          height: variants.length > 0 ? variants[0].height : null,
          mediaType: "hls",
          isMaster: true,
          variants,
          origin: location.origin,
          referer: location.href
        });
        return;
      }

      if (reportedUrls.has(absUrl)) return;
      reportedUrls.add(absUrl);

      const height = extractHeightFromText(absUrl);
      const durSec = typeof textSample === "string" ? estimateDurationSec(textSample) : 0;
      const label = height ? buildVariantLabel(height, null, null, durSec) : "HLS Video Stream (MP4)";

      broadcastPayload({
        __nahrStreamDetected: true,
        url: absUrl,
        label,
        height: height || null,
        mediaType: "hls",
        isMaster: false,
        origin: location.origin,
        referer: location.href
      });

      if (height) {
        tryProbeSiblingsFromMainWorld(absUrl, height, durSec);
      }
    } catch {}
  }

  function scanJsonForStreams(text) {
    try {
      if (!text || text.length > 300000) return;
      if (!text.includes(".m3u8") && !text.includes("/hls/")) return;
      const urlRx = /https?:\/\/[^\s"'<>\\]+(?:\.m3u8|\/hls\/[^\s"'<>\\]+)[^\s"'<>\\]*/gi;
      for (const m of text.matchAll(urlRx)) {
        const candidate = m[0].replace(/\\+/g, "");
        if (/\.m3u8(\?|#|$)/i.test(candidate)) {
          notifyStream(candidate, "");
        }
      }
    } catch {}
  }

  // 1. Passive fetch() wrapper (non-async pass-through so page fetch errors never create
  // an extra extension stack frame or unhandled rejection in chrome://extensions)
  if (origFetch) {
    window.fetch = function (...args) {
      const reqUrl = typeof args[0] === "string" ? args[0] : args[0] && args[0].url;
      const promise = origFetch(...args);
      if (!reqUrl || /^(data|blob|about|javascript|chrome-extension):/i.test(reqUrl)) {
        return promise;
      }
      try {
        promise.then(
          (resp) => {
            try {
              if (!resp || !resp.clone) return;
              const ct = (resp.headers && resp.headers.get("content-type")) || "";
              const looksRelevant =
                /\.m3u8(\?|#|$)/i.test(reqUrl) ||
                /mpegurl|json|text|octet-stream/i.test(ct) ||
                /\/(master|playlist|index|manifest|hls|stream|sources|embed|ajax)/i.test(reqUrl) ||
                /\.(txt|m3u8|json)(\?|$)/i.test(reqUrl);

              if (looksRelevant) {
                resp
                  .clone()
                  .text()
                  .then((txt) => {
                    if (!txt) return;
                    if (txt.slice(0, 512).includes("#EXTM3U")) {
                      notifyStream(resp.url || reqUrl, txt.slice(0, 32768));
                    } else if (txt.includes(".m3u8")) {
                      scanJsonForStreams(txt);
                    }
                  })
                  .catch(() => {});
              }
            } catch {}
          },
          () => {}
        );
      } catch {}
      return promise;
    };
  }

  // 2. Passive XMLHttpRequest wrapper (used heavily by hls.js & JWPlayer)
  const origOpen = XMLHttpRequest.prototype.open;
  const origSend = XMLHttpRequest.prototype.send;

  XMLHttpRequest.prototype.open = function (method, url, ...rest) {
    try {
      this.__nahrReqUrl = typeof url === "string" ? url : String(url || "");
    } catch {}
    return origOpen.call(this, method, url, ...rest);
  };

  XMLHttpRequest.prototype.send = function (...args) {
    try {
      this.addEventListener(
        "load",
        function () {
          try {
            const finalUrl = this.responseURL || this.__nahrReqUrl || "";
            if (this.responseType === "" || this.responseType === "text") {
              const txt = typeof this.responseText === "string" ? this.responseText.slice(0, 32768) : "";
              if (txt.slice(0, 512).includes("#EXTM3U")) {
                notifyStream(finalUrl, txt);
              } else if (txt.includes(".m3u8")) {
                scanJsonForStreams(txt);
              }
            } else if (/\.m3u8(\?|#|$)/i.test(finalUrl)) {
              notifyStream(finalUrl, "");
            }
          } catch {}
        },
        { once: true, passive: true }
      );
    } catch {}
    return origSend.apply(this, args);
  };
})();
