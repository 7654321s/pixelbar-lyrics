// ==UserScript==
// @name         PixelBar 视频字幕同步 (YouTube / B站)
// @namespace    https://github.com/7654321s/pixelbar-lyrics
// @version      1.0.0
// @description  抓取 YouTube 和 B 站网页视频 CC 软字幕，实时推送到本地 PixelBarLyrics 并在花再 Halo PixelBar 音响屏幕显示
// @match        *://www.youtube.com/*
// @match        *://www.bilibili.com/*
// @grant        GM_xmlhttpRequest
// @connect      127.0.0.1
// @run-at       document-idle
// ==/UserScript==

(function () {
    "use strict";

    const API_BASE = "http://127.0.0.1:18421";
    const DEBOUNCE_MS = 220;
    const MIN_SEND_INTERVAL_MS = 250;
    const TTL_MS = 6000;

    let lastSentText = "";
    let lastSendTime = 0;
    let debounceTimer = null;

    function getSourceName() {
        return location.hostname.includes("bilibili.com") ? "bilibili" : "youtube";
    }

    function normalizeSubtitle(raw) {
        return (raw || "")
            .replace(/\r?\n+/g, " ")
            .replace(/\s{2,}/g, " ")
            .trim();
    }

    function readDomSubtitle() {
        if (location.hostname.includes("youtube.com")) {
            const segs = document.querySelectorAll(".ytp-caption-window-container .ytp-caption-segment");
            if (segs.length > 0) {
                const text = Array.from(segs).map((el) => el.textContent || "").join(" ");
                const clean = normalizeSubtitle(text);
                if (clean) return clean;
            }
        }

        if (location.hostname.includes("bilibili.com")) {
            const selectors = [
                ".bpx-player-subtitle-panel-text",
                ".bili-subtitle-x-subtitle-panel-text"
            ];
            for (const sel of selectors) {
                const nodes = document.querySelectorAll(sel);
                if (nodes.length > 0) {
                    const text = Array.from(nodes).map((el) => el.textContent || "").join(" ");
                    const clean = normalizeSubtitle(text);
                    if (clean) return clean;
                }
            }
        }

        // 通用 <video> textTracks 兜底
        const video = document.querySelector("video");
        if (video && video.textTracks) {
            for (const track of video.textTracks) {
                if (track.mode === "showing" && track.activeCues && track.activeCues.length > 0) {
                    const text = Array.from(track.activeCues).map((c) => c.text || "").join(" ");
                    const clean = normalizeSubtitle(text);
                    if (clean) return clean;
                }
            }
        }

        return "";
    }

    function sendText(text) {
        const now = Date.now();
        if (!text || text === lastSentText) return;
        if (now - lastSendTime < MIN_SEND_INTERVAL_MS) return;

        lastSentText = text;
        lastSendTime = now;

        GM_xmlhttpRequest({
            method: "POST",
            url: `${API_BASE}/text`,
            headers: { "Content-Type": "application/json" },
            data: JSON.stringify({
                text: text,
                source: getSourceName(),
                ttlMs: TTL_MS
            }),
            timeout: 1500,
            onerror: () => { /* 本地服务未运行时静默忽略 */ },
            ontimeout: () => {}
        });
    }

    function scheduleCheck() {
        if (debounceTimer) clearTimeout(debounceTimer);
        debounceTimer = setTimeout(() => {
            const current = readDomSubtitle();
            if (current) {
                sendText(current);
            }
        }, DEBOUNCE_MS);
    }

    const observer = new MutationObserver(scheduleCheck);
    observer.observe(document.body, {
        childList: true,
        subtree: true,
        characterData: true
    });

    // 定期保底检查（应对 textTracks 变化不触发 DOM 变动的情况）
    setInterval(scheduleCheck, 500);
})();
