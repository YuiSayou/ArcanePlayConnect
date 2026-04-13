/**
 * ArcanePlayConnect - Overlay Core Engine
 *
 * Provides per-streamer channel isolation, real-time data streaming,
 * and shared DOM-diffing utilities for all overlay types.
 *
 * Architecture:
 *   - Each streamer gets a unique channel (streamerId)
 *   - The overlay reads config from URL params: ?streamer=...&overlay=...&theme=...
 *   - PRIMARY: WebSocket connection to Durable Object for instant push updates
 *   - FALLBACK: HTTP GET polling if WebSocket is unavailable
 *   - The desktop app pushes data via HTTP POST to the same Durable Object
 *   - No port forwarding required!
 *   - One overlay instance per streamer - no cross-contamination
 *
 * Quota optimization (WebSocket + Hibernatable Durable Object):
 *   - Zero KV reads/writes (Durable Object holds data in memory)
 *   - Zero polling from overlays (WebSocket receives instant push)
 *   - Only desktop app POST requests count as Worker invocations
 *   - Single long-lived WebSocket connection per overlay browser tab
 *   - Hibernatable WS: DO sleeps between messages, minimal billable duration
 *   - Client pings at 4min intervals to avoid unnecessary DO wake-ups
 */

'use strict';

const ArcaneOverlay = (() => {

    // -- URL Parameter Parsing --
    function getParams() {
        const params = new URLSearchParams(window.location.search);
        return {
            streamerId: params.get('streamer')  || '',
            overlayId:  params.get('overlay')   || '',
            theme:      params.get('theme')     || 'cyberpunk',
            maxPlayers: parseInt(params.get('max') || '5', 10),
            refresh:    parseInt(params.get('refresh') || '2000', 10),
            stats:      (params.get('stats') || 'HP,DMG,KILLS').split(',').map(s => s.trim().toUpperCase()),
            relay:      params.get('relay')     || '',  // Worker URL for Durable Object relay
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

    // -- Endpoint Builders --
    // Data and WebSocket endpoints point to the separate Cloudflare Worker
    // (Durable Object relay), not the Pages static site origin.
    function buildDataUrl(relayUrl, streamerId, overlayId) {
        var base = relayUrl.replace(/\/+$/, '');
        return base + '/api/data/' + encodeURIComponent(streamerId) + '/' + encodeURIComponent(overlayId);
    }

    function buildWsUrl(relayUrl, streamerId, overlayId) {
        var base = relayUrl.replace(/\/+$/, '').replace(/^http/, 'ws');
        return base + '/api/ws/' + encodeURIComponent(streamerId) + '/' + encodeURIComponent(overlayId);
    }

    // -- HTTP Fetch (fallback) --
    async function fetchData(url, timeoutMs, etag) {
        const controller = new AbortController();
        const timer = setTimeout(() => controller.abort(), timeoutMs || 5000);

        try {
            const headers = { 'Accept': 'application/json' };
            if (etag) headers['If-None-Match'] = '"' + etag + '"';

            const response = await fetch(url, {
                method: 'GET',
                signal: controller.signal,
                headers: headers,
            });

            clearTimeout(timer);

            if (response.status === 304) {
                return { notModified: true, etag: etag };
            }

            if (!response.ok) throw new Error('HTTP ' + response.status);

            const newEtag = (response.headers.get('ETag') || '').replace(/"/g, '');
            const data = await response.json();
            return { notModified: false, data: data, etag: newEtag || null };
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
            var playerCount = 0;
            if (data) {
                if (Array.isArray(data.players)) playerCount = data.players.length;
                else if (Array.isArray(data.gifts)) playerCount = data.gifts.length;
            }
            if (playerCount > 0) hasReceivedData = true;

            if (hasReceivedData) {
                connectionState = 'connected';
                statusEl.className = 'connection-status connected';
                statusEl.textContent = '\u25CF LIVE';
                setTimeout(() => { if (statusEl) statusEl.style.opacity = '0.3'; }, 3000);
            } else {
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

    // =====================================================================
    //  WebSocket-First Streaming Engine
    //
    //  1. Tries to connect via WebSocket to the Durable Object
    //  2. On success: receives instant push updates, zero polling
    //  3. On failure / unsupported: falls back to adaptive HTTP polling
    //  4. Auto-reconnects WebSocket on disconnect with exponential backoff
    // =====================================================================

    function startStreaming(config, renderFn) {
        const { streamerId, overlayId, refresh, relay } = config;

        if (!relay) {
            showError('Missing "relay" parameter. The Worker relay URL is required for cloud overlays.');
            return function() {};
        }

        const wsUrl = buildWsUrl(relay, streamerId, overlayId);
        const dataUrl = buildDataUrl(relay, streamerId, overlayId);
        let running = true;

        // WebSocket state
        let ws = null;
        let wsConnected = false;
        let wsReconnectDelay = 1000; // start at 1s, exponential backoff
        let wsReconnectTimer = null;
        let wsPingTimer = null;

        // Fallback polling state
        let pollTimer = null;
        let lastEtag = null;
        const baseInterval = refresh || 2000;
        const maxPollInterval = Math.max(baseInterval * 5, 10000);
        let consecutiveNoChange = 0;

        createStatusIndicator();

        // -- WebSocket Connection --

        function connectWebSocket() {
            if (!running) return;

            try {
                ws = new WebSocket(wsUrl);
            } catch (e) {
                // WebSocket not supported or blocked - fall back to polling
                startFallbackPolling();
                return;
            }

            ws.onopen = function() {
                wsConnected = true;
                wsReconnectDelay = 1000; // reset backoff on success

                // Stop fallback polling if it was running
                stopFallbackPolling();

                // Ping/pong keepalive every 4 minutes.
                // With Hibernatable WebSockets, each ping wakes the DO and costs
                // billable duration. Cloudflare keeps WS connections alive for ~10min
                // idle, so 4min is safe while minimizing DO wake-ups.
                clearInterval(wsPingTimer);
                wsPingTimer = setInterval(function() {
                    if (ws && ws.readyState === WebSocket.OPEN) {
                        try { ws.send('ping'); } catch { /* ignore */ }
                    }
                }, 240000);

                updateConnectionStatus(true, null);
            };

            ws.onmessage = function(event) {
                if (!running) return;
                var msg = event.data;

                // Ignore pong responses
                if (msg === 'pong') return;

                // Parse JSON data pushed by the Durable Object
                try {
                    var data = JSON.parse(msg);
                    updateConnectionStatus(true, data);
                    renderFn(data);
                } catch (e) {
                    // Malformed message - ignore
                }
            };

            ws.onclose = function() {
                wsConnected = false;
                clearInterval(wsPingTimer);
                wsPingTimer = null;

                if (!running) return;

                updateConnectionStatus(false, null);

                // Reconnect WebSocket with exponential backoff.
                // No fallback polling - each HTTP poll burns a Worker + DO request.
                // The WS reconnect is free once established.
                clearTimeout(wsReconnectTimer);
                wsReconnectTimer = setTimeout(function() {
                    connectWebSocket();
                }, wsReconnectDelay);
                wsReconnectDelay = Math.min(wsReconnectDelay * 2, 30000); // cap at 30s
            };

            ws.onerror = function() {
                // onerror is always followed by onclose, so reconnect happens there
            };
        }

        // -- Fallback HTTP Polling (used while WS is disconnected) --

        function startFallbackPolling() {
            if (pollTimer || !running) return;

            async function poll() {
                if (!running || wsConnected) {
                    pollTimer = null;
                    return;
                }

                try {
                    var result = await fetchData(dataUrl, 5000, lastEtag);
                    if (result.notModified) {
                        updateConnectionStatus(true, null);
                        consecutiveNoChange++;
                    } else {
                        lastEtag = result.etag;
                        updateConnectionStatus(true, result.data);
                        renderFn(result.data);
                        consecutiveNoChange = 0;
                    }
                } catch (err) {
                    updateConnectionStatus(false, null);
                    consecutiveNoChange = 0;
                }

                if (running && !wsConnected) {
                    var interval = consecutiveNoChange > 0
                        ? Math.min(Math.round(baseInterval * (1 + 0.5 * consecutiveNoChange)), maxPollInterval)
                        : baseInterval;
                    pollTimer = setTimeout(poll, interval);
                } else {
                    pollTimer = null;
                }
            }

            poll();
        }

        function stopFallbackPolling() {
            clearTimeout(pollTimer);
            pollTimer = null;
        }

        // -- Start! --
        connectWebSocket();

        // Return stop function
        return function() {
            running = false;
            clearTimeout(wsReconnectTimer);
            clearInterval(wsPingTimer);
            stopFallbackPolling();
            if (ws) {
                try { ws.close(); } catch { /* ignore */ }
                ws = null;
            }
        };
    }

    // Keep legacy startPolling as alias for backward compat (used by gift walls etc.)
    function startPolling(config, renderFn) {
        return startStreaming(config, renderFn);
    }

    // -- Error Page --
    function showError(message) {
        document.body.innerHTML =
            '<div style="display:flex;align-items:center;justify-content:center;height:100vh;' +
            'font-family:\'Rajdhani\',sans-serif;color:#ff3278;text-align:center;padding:40px;">' +
            '<div>' +
            '<div style="font-size:48px;margin-bottom:16px;">\u26A0\uFE0F</div>' +
            '<h2 style="font-family:\'Orbitron\',sans-serif;font-size:20px;margin-bottom:12px;' +
            'letter-spacing:3px;">CONFIGURATION ERROR</h2>' +
            '<p style="color:#8888aa;font-size:14px;max-width:400px;line-height:1.6;">' + escHtml(message) + '</p>' +
            '</div></div>';
    }

    // -- Initialization --
    function init(renderFn) {
        const params = getParams();

        if (!params.streamerId || !isValidStreamerId(params.streamerId)) {
            showError('Missing or invalid "streamer" parameter. Must be 3-64 alphanumeric characters.');
            return null;
        }

        if (!params.overlayId) {
            showError('Missing "overlay" parameter. This should be your overlay configuration ID.');
            return null;
        }

        if (!params.relay) {
            showError('Missing "relay" parameter. The Worker relay URL is required for cloud overlays.');
            return null;
        }

        applyTheme(params.theme);

        const stop = startStreaming(params, renderFn);

        return { params, stop };
    }

    // -- Public API --
    return {
        init,
        getParams,
        applyTheme,
        startPolling,
        startStreaming,
        fetchData,
        buildDataUrl,
        buildWsUrl,
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
