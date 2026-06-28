# Companion App Integration Guide

This document explains how the "companion app" feature works in BCUK Bot, so a
local desktop/PC companion app can be built and configured to talk to it. It
covers the two ways a client gets credentials (OAuth login or manual token),
how to receive live events, and what the bot operator needs to configure.

## What it does

Each Discord user who is set up with the bot can run a small local companion
app on their PC. That app:

1. Authenticates once (via Discord OAuth or a manually-issued token) and gets
   a long-lived **bearer token**.
2. Opens a persistent **Server-Sent Events (SSE)** connection using that
   token and receives a live push every time one of their Twitch channel-point
   rewards is redeemed.

There is no polling — the bot pushes events to the app the moment a
redemption happens via Twitch EventSub.

## Architecture overview

```
 Companion App (local PC)                    BCUK Bot (server)
┌────────────────────────┐                  ┌───────────────────────────────┐
│ 1. Loopback HTTP server│  GET /companion/login?redirect_uri=...&state=... │
│    on 127.0.0.1:<port> │ ───────────────────────────────────────────────► │
│                        │                  │  -> redirects into normal     │
│                        │                  │     Discord OAuth login       │
│                        │                  │  -> on success, redirects     │
│  GET /callback?code=.. │ ◄─────────────────────  back to redirect_uri     │
│                        │  with one-time `code` + `state`                  │
│                        │                  │                               │
│ 2. POST code to        │  POST /api/companion/oauth/token  { code }       │
│    exchange for token  │ ───────────────────────────────────────────────► │
│                        │ ◄─────────────────────────────────────────────── │
│   { token: "..." }     │                  │                               │
│                        │                  │                               │
│ 3. Store token locally,│  GET /api/companion/events                       │
│    open SSE stream     │  Authorization: Bearer <token>                   │
│                        │ ───────────────────────────────────────────────► │
│   receives:            │ ◄─────────────────────────────────────────────── │
│   data: {...redemption}│   text/event-stream, one push per redemption     │
└────────────────────────┘                  └───────────────────────────────┘
```

Server-side pieces (for reference, not needed to build the client):

| File | Role |
|---|---|
| `src/web/routes/companionAuth.ts` | Loopback OAuth login entry point + code-for-token exchange |
| `src/web/routes/companionKeys.ts` | Dashboard page to view/issue/revoke a token manually (no app needed) |
| `src/web/routes/companionEvents.ts` | The SSE endpoint and in-memory push fan-out |
| `src/web/middleware.ts` (`requireCompanionKey`) | Bearer-token auth for the two API routes above |
| `src/db/companionTokens.ts`, `src/db/companionOAuthCodes.ts` | Token/code storage (only SHA-256 hashes are persisted, never plaintext) |
| `src/twitch/eventsub/twitchEventSubHandler.ts` (`handleRedemption`) | Fires the push on every channel-point redemption |

## Option A — OAuth login flow (recommended for a real app)

This is the RFC 8252 "loopback interception" pattern used by CLI/desktop
OAuth clients (same approach as `gcloud`, GitHub CLI, etc.).

**Step 1 — App starts a temporary local HTTP server**

The companion app spins up a tiny HTTP server on `127.0.0.1` on any free
port (e.g. `http://127.0.0.1:53127/callback`). It generates a random opaque
`state` string for CSRF binding and keeps it in memory.

**Step 2 — App opens the user's browser to the bot's login URL**

```
GET https://<bot-host>/companion/login?redirect_uri=http://127.0.0.1:53127/callback&state=<random>
```

Requirements enforced server-side (`companionAuth.ts`):
- `redirect_uri` **must** be `http://127.0.0.1:<any-port>` or
  `http://localhost:<any-port>` — anything else is rejected with a 400. This
  is a hard security requirement; non-loopback redirects are never accepted.
- `state` is required (any non-empty string) and is echoed back verbatim.

The server stashes `redirect_uri` + `state` on the user's browser session
(10-minute TTL) and redirects into the bot's normal Discord OAuth login.

**Step 3 — User logs in with Discord as usual**

Nothing the companion app needs to do here — it's the bot's existing
`/auth/discord` → Discord → `/auth/discord/callback` flow, just running in
the user's regular browser.

**Step 4 — Bot redirects back to the companion app with a one-time code**

Once Discord login succeeds, the callback detects the pending companion
OAuth state and — instead of creating a normal dashboard session — redirects
the browser to:

```
http://127.0.0.1:53127/callback?code=<one-time-code>&state=<same-state>
```

The companion app's local HTTP server receives this request. It **must**
verify the returned `state` matches what it generated in Step 1 before
proceeding.

**Step 5 — App exchanges the code for a long-lived token**

```
POST https://<bot-host>/api/companion/oauth/token
Content-Type: application/json

{ "code": "<one-time-code>" }
```

Response:
```json
{ "token": "<64-char hex string>" }
```

Notes:
- The code is single-use and expires after **60 seconds** — exchange it
  immediately.
- On failure (expired/already used/invalid) the response is `400` with
  `{ "ok": false, "error": "Invalid or expired code" }`.
- The app should store the returned token securely (OS keychain, or at
  minimum a file with restrictive permissions) — it does not expire and is
  the only credential needed from here on.

## Option B — Manual token (no app-side OAuth needed)

For simpler setups, the user can skip the OAuth dance entirely:

1. User logs into the bot's web dashboard normally and visits **Companion
   App** in the nav (`/companion-key`).
2. Clicking "Issue Token" generates a new token and displays it **once** —
   it is never shown again, only its hash is stored.
3. User copies the token and pastes it into the companion app's settings.
4. Issuing a new token automatically invalidates the previous one (one
   active token per user). The same page has a "Revoke" button.

This is the path to recommend if you don't want to build the loopback HTTP
server / browser-launch logic at all — just give users a paste-a-token field.

## Receiving events (both options converge here)

Once the app has a token (from either flow), it opens a persistent SSE
connection:

```
GET https://<bot-host>/api/companion/events
Authorization: Bearer <token>
```

Behavior:
- Responds with `Content-Type: text/event-stream` and stays open.
- Sends a comment-only keepalive (`: ping`) every 25 seconds — treat any
  receipt of data on the connection as "still alive."
- Sends a real event whenever the user's Twitch channel has a channel-point
  redemption:
  ```
  data: {"type":"channel_points_redemption","rewardId":"...","rewardTitle":"...","userLogin":"...","userName":"...","userInput":"...","redeemedAt":"2026-06-27T12:34:56.000Z"}

  ```
- `401` if the bearer token is missing/invalid/revoked — the app should stop
  and prompt the user to re-authenticate (the page/manual flow above) in
  that case.
- `429` if the same token already has the maximum number of concurrent SSE
  connections open (default **3**, configurable server-side via
  `COMPANION_MAX_SSE_PER_TOKEN`) — e.g. if the app is running on multiple
  machines with the same token, or didn't clean up a dead connection.
- The app should implement reconnect-with-backoff if the connection drops
  (standard SSE client behavior — most SSE/EventSource libraries do this
  automatically).

## Error reference

| Status | When | App should... |
|---|---|---|
| 400 (`/companion/login`) | Missing/invalid `redirect_uri` or `state` | Fix the request — this is a client bug, not a runtime condition |
| 400 (`/api/companion/oauth/token`) | Code missing, expired (>60s), or already used | Restart the login flow from Step 2 |
| 401 (`/api/companion/events`) | Token missing, revoked, or never issued | Prompt the user to re-authenticate / re-issue a token |
| 429 (`/api/companion/events`) | Too many concurrent connections for this token | Close other connections, or back off and retry |
| 500 | Server-side DB error | Retry with backoff |

## Server-side configuration (for whoever deploys the bot)

No new required environment variables — the feature works out of the box
once the migration below is applied. One optional tunable:

| Env var | Default | Purpose |
|---|---|---|
| `COMPANION_MAX_SSE_PER_TOKEN` | `3` | Max concurrent SSE connections per token (per-user, not global) |

**Database migration required** (`migrations/companion_app_tokens.sql`) —
adds two tables:
- `companion_app_tokens` — one row per Discord user, stores only the
  SHA-256 hash of their current token plus `created_at`/`revoked_at`.
- `companion_oauth_codes` — short-lived one-time codes used only during the
  OAuth exchange (60s TTL, single-use).

Run this against the bot's database before the feature will work.

## Security properties worth telling the client about

- Only token/code **hashes** are ever stored — even with DB access, no one
  can recover a usable token from storage.
- The OAuth `redirect_uri` is restricted to loopback addresses
  (`127.0.0.1`/`localhost`) only, per RFC 8252 — prevents the authorization
  code from being redirected to an attacker-controlled host.
- The one-time code exchange and token issuance happen in a single DB
  transaction — a code can't be "burned" without successfully producing a
  usable token.
- A user has exactly one active token at a time; issuing a new one
  (via either flow) immediately invalidates the old one.
