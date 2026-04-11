/**
 * ArcanePlayConnect - Cloudflare Pages Function
 *
 * Data relay endpoint: /api/data/{streamerId}/{overlayId}
 *
 * - POST / PUT: Desktop app pushes overlay JSON data (authenticated with a push token)
 * - GET:        Overlay pages read overlay JSON data (public, same-origin)
 *
 * Uses Cloudflare KV namespace bound as OVERLAY_DATA.
 *
 * KV key format: "data:{streamerId}:{overlayId}"
 * TTL: 5 minutes (auto-cleanup if app stops pushing)
 *
 * IMPORTANT: Cloudflare Pages Functions require explicit per-method exports
 * (onRequestGet, onRequestPost, etc.) for non-GET methods to be routed
 * correctly. A single onRequest export only handles GET reliably.
 */

// Max data size: 100KB
const MAX_DATA_SIZE = 100 * 1024;
// KV TTL in seconds (data expires if not refreshed)
const DATA_TTL = 300; // 5 minutes

function isValidId(id) {
    return typeof id === 'string' && /^[a-zA-Z0-9_-]{3,64}$/.test(id);
}

function corsHeaders() {
    return {
        'Access-Control-Allow-Origin': '*',
        'Access-Control-Allow-Methods': 'GET, POST, PUT, OPTIONS',
        'Access-Control-Allow-Headers': 'Content-Type, X-Push-Token',
        'Access-Control-Max-Age': '86400',
    };
}

function jsonResponse(body, status = 200) {
    return new Response(JSON.stringify(body), {
        status,
        headers: { 'Content-Type': 'application/json', ...corsHeaders() },
    });
}

/**
 * Parses and validates the path segments from the request.
 * Returns { streamerId, overlayId, kvKey } or a Response on error.
 */
function parsePath(params) {
    const pathParts = params.path || [];
    if (pathParts.length !== 2) {
        return { error: jsonResponse({ error: 'Expected /api/data/{streamerId}/{overlayId}' }, 400) };
    }

    const [streamerId, overlayId] = pathParts;

    if (!isValidId(streamerId) || !isValidId(overlayId)) {
        return { error: jsonResponse({ error: 'Invalid streamer or overlay ID' }, 400) };
    }

    return { streamerId, overlayId, kvKey: `data:${streamerId}:${overlayId}` };
}

// ---------------------------------------------------------------------------
//  GET - Read overlay data (public)
// ---------------------------------------------------------------------------
export async function onRequestGet(context) {
    const { params, env } = context;
    const parsed = parsePath(params);
    if (parsed.error) return parsed.error;

    const kv = env.OVERLAY_DATA;
    if (!kv) {
        return jsonResponse({ error: 'KV not configured' }, 500);
    }

    const data = await kv.get(parsed.kvKey, 'text');
    if (!data) {
        return new Response(JSON.stringify({ players: [], timestamp: Date.now() }), {
            status: 200,
            headers: {
                'Content-Type': 'application/json',
                'Cache-Control': 'no-cache',
                ...corsHeaders(),
            },
        });
    }

    return new Response(data, {
        status: 200,
        headers: {
            'Content-Type': 'application/json',
            'Cache-Control': 'no-cache',
            ...corsHeaders(),
        },
    });
}

// ---------------------------------------------------------------------------
//  POST / PUT - Push overlay data (authenticated with push token)
// ---------------------------------------------------------------------------
async function handlePush(context) {
    const { request, params, env } = context;
    const parsed = parsePath(params);
    if (parsed.error) return parsed.error;

    const kv = env.OVERLAY_DATA;
    if (!kv) {
        return jsonResponse({ error: 'KV not configured' }, 500);
    }

    // Validate push token
    const pushToken = request.headers.get('X-Push-Token') || '';
    const tokenKey = `token:${parsed.streamerId}`;

    const storedToken = await kv.get(tokenKey, 'text');
    if (storedToken && storedToken !== pushToken) {
        return jsonResponse({ error: 'Invalid push token' }, 403);
    }

    if (!storedToken && pushToken) {
        await kv.put(tokenKey, pushToken, { expirationTtl: DATA_TTL * 2 });
    }

    // Read and validate body
    const body = await request.text();
    if (body.length > MAX_DATA_SIZE) {
        return jsonResponse({ error: 'Data too large' }, 413);
    }

    try {
        JSON.parse(body);
    } catch {
        return jsonResponse({ error: 'Invalid JSON' }, 400);
    }

    // Store in KV with TTL
    await kv.put(parsed.kvKey, body, { expirationTtl: DATA_TTL });

    // Refresh token TTL
    if (pushToken) {
        await kv.put(tokenKey, pushToken, { expirationTtl: DATA_TTL * 2 });
    }

    return jsonResponse({ ok: true });
}

export async function onRequestPost(context) {
    return handlePush(context);
}

export async function onRequestPut(context) {
    return handlePush(context);
}

// ---------------------------------------------------------------------------
//  OPTIONS - CORS preflight
// ---------------------------------------------------------------------------
export async function onRequestOptions() {
    return new Response(null, { status: 204, headers: corsHeaders() });
}

// ---------------------------------------------------------------------------
//  Fallback catch-all - routes by method for maximum compatibility
//  Some Cloudflare Pages deployments may use onRequest instead of per-method.
// ---------------------------------------------------------------------------
export async function onRequest(context) {
    const method = context.request.method.toUpperCase();
    switch (method) {
        case 'GET':     return onRequestGet(context);
        case 'POST':    return onRequestPost(context);
        case 'PUT':     return onRequestPut(context);
        case 'OPTIONS': return onRequestOptions(context);
        default:
            return jsonResponse({ error: 'Method not allowed' }, 405);
    }
}
