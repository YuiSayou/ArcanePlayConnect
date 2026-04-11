/**
 * ArcanePlayConnect - Overlay Core Engine
 * 
 * Provides per-streamer channel isolation, secure data fetching,
 * and shared DOM-diffing utilities for all overlay types.
 * 
 * Architecture:
 *   - Each streamer gets a unique channel (streamerId)
 *   - The overlay reads config from URL params: ?streamer=...&overlay=...&theme=...
 *   - Data is read from the same Cloudflare origin via Pages Functions relay:
 *     GET /api/data/{streamerId}/{overlayId}
 *   - The desktop app pushes data to the same endpoint via PUT
 *   - No port forwarding required!
 *   - One overlay instance per streamer - no cross-contamination
 */

'use strict';

const ArcaneOverlay = (() => {

    // -- URL Parameter Parsing --
    function getParams() {
        const params = new URLSearchParams(window.location.search);
        return {
            streamerId: params.get('streamer')  || '',   // unique streamer channel ID
            overlayId:  params.get('overlay')   || '',   // overlay config ID
            theme:      params.get('theme')     || 'cyberpunk',
            maxPlayers: parseInt(params.get('max') || '5', 10),
            refresh:    parseInt(params.get('refresh') || '2000', 10),
            stats:      (params.get('stats') || 'HP,DMG,KILLS').split(',').map(s => s.trim().toUpperCase()),
        };
    }

    // -- Theme Application --
    function applyTheme(theme) {
        document.documentElement.setAttribute('data-theme', (theme || 'cyberpunk').toLowerCase());
    }

    // -- Validation --
    function isValidStreamerId(id) {
        return typeof id === 'string' && /^[a-zA-Z0-9_-]{3,64}$/.test(id);
    }

    // -- Data Endpoint Builder --
    // Data is served from the same Cloudflare origin via Pages Functions
    function buildDataUrl(streamerId, overlayId) {
        return `/api/data/${encodeURIComponent(streamerId)}/${encodeURIComponent(overlayId)}`;
    }

    // -- Secure Fetch with Timeout --
    async function fetchData(url, timeoutMs = 5000) {
        const controller = new AbortController();
        const timer = setTimeout(() => controller.abort(), timeoutMs);

        try {
            const response = await fetch(url, {
                method: 'GET',
                signal: controller.signal,
                headers: { 'Accept': 'application/json' },
            });

            clearTimeout(timer);

            if (!response.ok) {
                throw new Error(`HTTP ${response.status}`);
            }

            return await response.json();
        } catch (err) {
            clearTimeout(timer);
            throw err;
        }
    }

    // -- Connection Status Indicator --
    let statusEl = null;
    let connectionState = 'disconnected';
    let consecutiveErrors = 0;
    let hasReceivedData = false;

    function createStatusIndicator() {
        statusEl = document.createElement('div');
        statusEl.className = 'connection-status connecting';
        statusEl.textContent = 'CONNECTING...';
        document.body.appendChild(statusEl);
    }

    function updateConnectionStatus(success, data) {
        if (!statusEl) return;
        if (success) {
            consecutiveErrors = 0;
            // Check if we have actual player/gift data or just an empty response
            var playerCount = 0;
            if (data) {
                if (Array.isArray(data.players)) playerCount = data.players.length;
                else if (Array.isArray(data.gifts)) playerCount = data.gifts.length;
            }
            if (playerCount > 0) hasReceivedData = true;

            if (hasReceivedData) {
                // We have received real data at least once - show LIVE
                connectionState = 'connected';
                statusEl.className = 'connection-status connected';
                statusEl.textContent = '\u25CF LIVE';
                setTimeout(() => { if (statusEl) statusEl.style.opacity = '0.3'; }, 3000);
            } else {
                // API responds OK but no data pushed yet - show WAITING
                connectionState = 'connecting';
                statusEl.className = 'connection-status connecting';
                statusEl.textContent = '\u25CF WAITING FOR DATA';
            }
        } else {
            consecutiveErrors++;
            statusEl.style.opacity = '1';
            if (consecutiveErrors >= 3) {
                connectionState = 'disconnected';
                statusEl.className = 'connection-status disconnected';
                statusEl.textContent = '\u25CF OFFLINE';
            } else {
                connectionState = 'connecting';
                statusEl.className = 'connection-status connecting';
                statusEl.textContent = '\u25CF RECONNECTING...';
            }
        }
    }

    // -- DOM Diffing Helpers --
    function escHtml(s) {
        const d = document.createElement('div');
        d.textContent = s;
        return d.innerHTML;
    }

    function setText(el, selector, text) {
        const t = el.querySelector(selector);
        if (t && t.textContent !== text) t.textContent = text;
    }

    function setAttr(el, selector, attr, val) {
        const t = el.querySelector(selector);
        if (t && t.getAttribute(attr) !== val) t.setAttribute(attr, val);
    }

    function setClass(el, cls, on) {
        if (on) el.classList.add(cls);
        else el.classList.remove(cls);
    }

    function setRankClass(el, rank) {
        el.classList.remove('rank-1', 'rank-2', 'rank-3');
        if (rank >= 1 && rank <= 3) el.classList.add('rank-' + rank);
    }

    // -- Number Formatter --
    function fmt(n) {
        if (n >= 1000000) return (n / 1000000).toFixed(1) + 'M';
        if (n >= 1000) return (n / 1000).toFixed(1) + 'K';
        return String(n);
    }

    // -- Polling Engine --
    /**
     * Creates a polling loop that fetches data and calls the render callback.
     * @param {Object} config - { streamerId, overlayId, refresh }
     * @param {Function} renderFn - Called with (data) on each successful fetch
     * @returns {Function} stop - Call to stop polling
     */
    function startPolling(config, renderFn) {
        const { streamerId, overlayId, refresh } = config;
        const dataUrl = buildDataUrl(streamerId, overlayId);
        let running = true;

        createStatusIndicator();

        async function poll() {
            if (!running) return;

            try {
                const data = await fetchData(dataUrl);
                updateConnectionStatus(true, data);
                renderFn(data);
            } catch (err) {
                updateConnectionStatus(false, null);
            }

            if (running) {
                setTimeout(poll, refresh || 2000);
            }
        }

        poll();

        return () => { running = false; };
    }

    // -- Error Page --
    function showError(message) {
        document.body.innerHTML = `
            <div style="display:flex;align-items:center;justify-content:center;height:100vh;
                        font-family:'Rajdhani',sans-serif;color:#ff3278;text-align:center;padding:40px;">
                <div>
                    <div style="font-size:48px;margin-bottom:16px;">\u26A0\uFE0F</div>
                    <h2 style="font-family:'Orbitron',sans-serif;font-size:20px;margin-bottom:12px;
                               letter-spacing:3px;">CONFIGURATION ERROR</h2>
                    <p style="color:#8888aa;font-size:14px;max-width:400px;line-height:1.6;">${escHtml(message)}</p>
                </div>
            </div>`;
    }

    // -- Initialization --
    /**
     * Initialize an overlay. Validates params, applies theme, starts polling.
     * @param {Function} renderFn - Called with (data) on each poll
     * @returns {{ params, stop }} or null if config invalid
     */
    function init(renderFn) {
        const params = getParams();

        // Validate required params
        if (!params.streamerId || !isValidStreamerId(params.streamerId)) {
            showError('Missing or invalid "streamer" parameter. Must be 3-64 alphanumeric characters.');
            return null;
        }

        if (!params.overlayId) {
            showError('Missing "overlay" parameter. This should be your overlay configuration ID.');
            return null;
        }

        // Apply theme
        applyTheme(params.theme);

        // Start polling - data comes from same origin via Cloudflare Pages Functions
        const stop = startPolling(params, renderFn);

        return { params, stop };
    }

    // -- Public API --
    return {
        init,
        getParams,
        applyTheme,
        startPolling,
        fetchData,
        buildDataUrl,
        showError,
        isValidStreamerId,

        // DOM helpers
        escHtml,
        setText,
        setAttr,
        setClass,
        setRankClass,
        fmt,
    };

})();
