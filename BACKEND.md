# BCA-Tracker Backend API Contract

Two features need backend support: **match data submission** (already built into the tracker) and **lobby directory** (new in this phase).

The tracker hits two endpoints. Implement these on your VPS however you like (FastAPI, Express, ASP.NET, whatever) — the tracker only cares about the wire format.

---

## Base URL

User configures this in **Settings → Data submission → Server endpoint**, e.g. `https://bca.example.com`. Both features share the same base URL.

All requests are HTTPS recommended, JSON bodies, UTF-8.

---

## 1. Match upload

### `POST {endpoint}/v1/matches`

The tracker uploads a finished match's JSON file as the request body.

**Headers:**
```
Content-Type: application/json
X-BCA-Account-Id: <32-char hex GUID>
X-BCA-Source-File: match_HH-mm-ss_<map>_<mode>.json
```

**Body:** the raw match JSON the tracker also saves to disk under `%AppData%\BCA-Tracker\matches\`. Contains scoreboard, kill feed, loadouts, stat counters. Schema is whatever `MatchRecord` serialises to — your backend can be permissive (store everything).

**Response:**
- `2xx` → success, tracker deletes the file from its upload queue
- `4xx` (other than 408/429) → permanent rejection, tracker moves the file to `upload-failed/`
- `5xx` or connection error → retried with exponential backoff (1s, 2s, 4s, 8s, 16s, 30s, 30s...), up to 10 attempts

---

## 2. Lobby directory

Hosts advertise themselves; everyone else fetches the list.

### `POST {endpoint}/v1/lobbies`

Heartbeat from a host. Called every **30 seconds** while the host is in a custom lobby with advertising enabled.

**Headers:**
```
Content-Type: application/json
```

**Body:**
```json
{
  "hostProfileId": "ab12cd34ef56...",
  "hostName": "Puppetino",
  "lobbyName": "Friday night customs",
  "mapRowName": "DA_LostComplex",
  "gameModeRowName": "GM_BackupTeam",
  "maxTeamSize": 3,
  "currentPlayerCount": 4,
  "hasPassword": false,
  "hostExternalIP": "203.0.113.45",
  "hostExternalPort": 27015
}
```

**Field notes:**
- `hostProfileId` is the host's stable per-player GUID (read from BCA memory). Use this as the primary key for the lobby record — repeated POSTs from the same host should **upsert**, not duplicate.
- `lobbyName` is host-chosen text, treat as untrusted, sanitise before display.
- `mapRowName` and `gameModeRowName` are BCA's internal identifiers; tracker resolves to display names client-side.
- `hostExternalIP:hostExternalPort` is what joiners paste into BCA's direct-connect screen.

**Response:** `2xx` for success. Body is ignored.

### `DELETE {endpoint}/v1/lobbies/{hostProfileId}`

Goodbye from a host. Called when advertising is toggled off or the tracker is closing. Backend should remove the lobby immediately.

**Response:** `2xx` for success.

### `GET {endpoint}/v1/lobbies`

Returns the list of currently-advertised lobbies. Called by the browser page on load and every 30s thereafter.

**Response:**
```json
[
  {
    "hostProfileId": "ab12cd34ef56...",
    "hostName": "Puppetino",
    "lobbyName": "Friday night customs",
    "mapRowName": "DA_LostComplex",
    "gameModeRowName": "GM_BackupTeam",
    "maxTeamSize": 3,
    "currentPlayerCount": 4,
    "hasPassword": false,
    "hostExternalIP": "203.0.113.45",
    "hostExternalPort": 27015
  },
  ...
]
```

Same shape as the POST body. Empty array for "no lobbies right now."

---

## Lobby expiration

The backend should **drop lobbies that haven't been heartbeated in 2 minutes** (i.e. 4 missed heartbeats at 30s cadence). This handles trackers that crash, hosts that quit BCA without toggling off, network drops, etc.

A background sweep every 30s deleting `WHERE last_heartbeat_utc < now() - interval '2 minutes'` is fine.

---

## Storage suggestion

For both endpoints, a single Postgres table per feature is plenty:

```sql
CREATE TABLE matches (
  id            SERIAL PRIMARY KEY,
  account_id    TEXT NOT NULL,
  source_file   TEXT NOT NULL,
  body          JSONB NOT NULL,
  received_at   TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX matches_account_idx ON matches(account_id);

CREATE TABLE lobbies (
  host_profile_id      TEXT PRIMARY KEY,
  host_name            TEXT NOT NULL,
  lobby_name           TEXT NOT NULL,
  map_row_name         TEXT NOT NULL,
  game_mode_row_name   TEXT NOT NULL,
  max_team_size        INT  NOT NULL,
  current_player_count INT  NOT NULL,
  has_password         BOOL NOT NULL,
  host_external_ip     TEXT NOT NULL,
  host_external_port   INT  NOT NULL,
  last_heartbeat_utc   TIMESTAMPTZ NOT NULL DEFAULT now()
);
```

For the lobby `POST`, do an `INSERT ... ON CONFLICT (host_profile_id) DO UPDATE SET ..., last_heartbeat_utc = now()`. For the `GET`, just `SELECT ... FROM lobbies WHERE last_heartbeat_utc > now() - interval '2 minutes'`.

---

## Rate limiting (optional but recommended)

- Match uploads: 60/min per `X-BCA-Account-Id` is generous (a real player can't finish a match a second).
- Lobby POST: 4/min per `host_profile_id` (heartbeat is 1 every 30s, give some headroom).
- Lobby GET: 60/min per IP.

---

## Abuse considerations

- `hostName` and `lobbyName` are user-typed — sanitise on display (you'd do this in any web frontend anyway).
- `hostExternalIP` is self-reported by the host. Don't trust it for anything security-sensitive; it's just a hint joiners use to connect.
- No auth on these endpoints by design — the tracker has no logins. If you want to ban a misbehaving host, blocklist their `hostProfileId` server-side.
