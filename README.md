# Arcane Connect

A Windows desktop app that connects **TikTok Live events** to a **Minecraft server**,
with stream overlays for OBS / TikTok Studio.

How it works: connect to a TikTok Live room (directly, or via a TikFinity webhook),
map events such as gifts, chats, follows, joins and likes to Minecraft commands, and
send them to the server over RCON. Optional live overlays (leaderboards, gift walls,
like rankings) can be shown locally or relayed through Cloudflare for use as browser
sources with no port forwarding.

## Status

**Alpha — Work in Progress.**

- The primary UI is implemented (dashboard, mappings, command buttons, creature
  arena, follower database, overlays, Minecraft/RCON setup, profiles, event log).
- Core functionality is implemented: TikTok Live intake, webhook intake, RCON
  command execution, action mappings, creature tracking, local + cloud overlays.
- Additional services, integrations and features are still being developed, and
  some planned functionality is not yet available.
- Expect rough edges, changing behavior, and incomplete areas. This is not
  production-ready software.

## Features

### Implemented

- **TikTok Live intake** — connect to a TikTok Live room by username and receive
  chat, gift, follow, join, like, share and subscribe events (via TikTokLiveSharp).
- **TikFinity webhook intake** — legacy/alternative local webhook listener
  (`http://localhost:{port}/event`) that parses TikFinity payloads.
- **Minecraft RCON control** — connect to a Minecraft (PaperMC-compatible) server
  over RCON with auto-reconnect and keep-alive; send commands from the UI.
- **Command engine with suggestions** — Minecraft command autocomplete/suggestion
  data and a command-builder UI.
- **Action mappings** — map triggers (gift, chat, follow, join, like, …) to
  Minecraft commands with placeholder substitution (e.g. player nicknames).
- **Command buttons** — saved one-shot and repeating (timer) command buttons,
  executable manually or from events, with a test mode.
- **Creature arena / tracker** — parse summons from events, track summoned
  creatures (HP / damage / kills) by polling the server.
- **Follower database** — persistent local JSON store of TikTok followers.
- **Profiles** — per-streamer profiles (TikTok user, RCON host/port, mappings,
  buttons) stored as local JSON, with migration of legacy formats.
- **Event log** — in-app categorized log (system, chat, gift, follow, join, like,
  webhook, …).
- **Local overlays** — built-in HTTP server (`http://localhost:{port}/overlay/{id}`)
  serving overlay pages and live JSON for OBS Browser Source.
- **Cloud overlays** — push overlay data to a Cloudflare Worker (Durable Object)
  relay; static overlay pages hosted on Cloudflare Pages read it via WebSocket
  (HTTP polling fallback). Overlay types: vertical/horizontal rankings, gift
  walls, like rankings, gift rankings; 10 themes; compact/minimal styles.

### In Development

- Additional services and third-party integrations beyond TikTok + RCON.
- Extended overlay types, themes and customization.
- Hardening of the cloud relay (see “Known Limitations”).

### Planned

- Future integrations and features as the project direction settles. Nothing
  listed here should be assumed to exist yet — see “Implemented” above for what
  actually works today.

## Technology

- .NET 8, C# (nullable enabled), XAML
- WinUI 3 via Windows App SDK 1.8 (unpackaged `WinExe`, `WindowsPackageType=None`)
- CommunityToolkit.Mvvm, WinUIEx, TikTokLiveSharp 1.2.3
- Cloudflare Pages (static overlay pages in `cloudflare-overlay/`) +
  Cloudflare Worker with Durable Object relay (WebSocket fan-out)
- Local persistence: JSON files under `%LocalAppData%\ArcanePlayConnect`
  (profiles, followers, saved commands, settings). No database server required.

Target: `net8.0-windows10.0.19041.0` (Windows 10 2004+). x86 / x64 / ARM64.

## Architecture

```text
TikTok Live ──▶ TikTokLiveService ──┐
TikFinity ────▶ WebhookListener ─────┼──▶ EventProcessor ──▶ RconService ──▶ Minecraft
                                     │
                                     ├──▶ CreatureTracker ──┐
                                     ├──▶ FollowerService   ├──▶ OverlayServer (localhost :7700)
                                     └──▶ LiveStatsTracker ──┘        │
                                                                      ▼ (optional cloud relay)
                                                     Cloudflare Worker (Durable Object)
                                                     Push:  POST /api/data/{streamer}/{overlay}
                                                     Overlay browsers:  WS /api/ws/{streamer}/{overlay}
                                                                       GET /api/data/{streamer}/{overlay} (fallback)
                                                     Static pages: Cloudflare Pages deployment
```

- All servers bind **localhost only** (`WebhookListenerService`,
  `OverlayServerService`). Nothing listens on the public network.
- Cloud pushes are authenticated per streamer channel with an `X-Push-Token`
  header derived locally from the streamer ID; overlay reads are public to
  anyone holding the streamer/overlay IDs (by design — overlay URLs are
  shareable browser-source links, like any stream overlay URL).
- RCON credentials are entered in the UI and kept in memory plus the local
  profile JSON. They are never compiled into the app.

## Setup

Prerequisites:

- Windows 10 version 2004 (build 19041) or later
- Visual Studio 2022 17.14+ with “.NET desktop development” and
  “Windows application development” workloads (or the .NET 8 SDK + Windows App
  SDK build tools)
- A Minecraft Java server with RCON enabled (`enable-rcon=true` in
  `server.properties`), reachable from this machine
- A TikTok account that is **live** when connecting

Run:

```powershell
dotnet restore
dotnet build ArcanePlayConnect.csproj -c Debug -p:Platform=x64
```

Then launch from Visual Studio (`ArcanePlayConnect (Unpackaged)` profile) or run
the built executable. Publish profiles for win-x86 / win-x64 / win-arm64 are in
`Properties/PublishProfiles/`.

No API keys, tokens, or secrets are required to build or run the app.

## Configuration

Everything is configured in the UI; there are no environment variables or secret
files.

| Setting | Where | Default |
|---|---|---|
| TikTok username | Profile settings | — (you provide it) |
| RCON host / port / password | Profile settings | `127.0.0.1` / `25575` / — (you provide it) |
| Webhook listen port | Profile (legacy) | `5000`, localhost only |
| Overlay local port | Overlays page | `7700`, localhost only |
| Cloud relay (`WorkerUrl`) | Per overlay | `https://arcaneplayconnect-relay.yuisayou.workers.dev` |
| Pages base URL (`CloudflareBaseUrl`) | Per overlay | `https://arcaneplayconnect.pages.dev` |

The default cloud URLs point at the author’s Cloudflare deployment so overlays
work out of the box. To self-host, deploy the static pages in
`cloudflare-overlay/` to your own Pages project and run a compatible relay
Worker implementing `POST`/`GET /api/data/{streamerId}/{overlayId}` and
`GET /api/ws/{streamerId}/{overlayId}` (headers `X-Push-Token`,
`X-Content-Hash`), then set the two URLs above in the overlay settings.

Local data (never committed to this repo) lives in
`%LocalAppData%\ArcanePlayConnect\` (`Profiles\*.json` including RCON
passwords, `followers.json`, `saved_commands.json`, `settings.json`).

## Development

- Solution: `../ArcanePlayConnect.sln` (one project: `ArcanePlayConnect`).
- `dotnet build` restores NuGet packages automatically.
- `bin/`, `obj/`, `*.user`, `*.Backup.tmp` and `Output-*.txt` are local build
  artifacts and are intentionally **not** tracked in Git.
- Logging goes to the in-app Event Log (`LoggingService`); webhook raw bodies
  are logged to the Webhook category for debugging.

## Known Limitations

- **Alpha.** Features may be incomplete, inconsistent, or change without notice.
- **Windows only.** WinUI 3 / Windows App SDK; no macOS/Linux support.
- TikTok connectivity depends on the third-party TikTokLiveSharp library and on
  TikTok itself; logins, rate limits, or site changes can break it.
- The default cloud relay is the author’s personal deployment with no SLA; for
  serious use, self-host (see above).
- Relay data channels are **not private**: anyone with your overlay’s
  streamer/overlay IDs can read the live stats, and channel writes are guarded
  only by the push token (first-writer-wins per relay instance). Treat overlay
  data as public; never push secrets through it.
- Local HTTP servers and stored RCON passwords assume a trusted local machine.

## Roadmap

Short, grounded direction (no dates promised):

- Finish in-progress services/integrations.
- More overlay types, themes and options.
- Relay hardening (stronger channel auth, rate limits) and documented
  self-hosting.
- General alpha cleanup driven by real streaming use.

## License

No license has been chosen yet. Until the owner adds one, all rights are
reserved — do not redistribute or reuse this code beyond what GitHub’s public
visibility permits.
