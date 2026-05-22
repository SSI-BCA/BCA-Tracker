using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BCATracker.Core;

namespace BCATracker.UI.Services;

/// <summary>
/// Orchestrates lobby hosting using NetBird as a virtual WireGuard LAN.
///
/// Flow when the user is hosting and advertising is on:
///   1. Memory says we're in a custom lobby + we're the host (or the
///      manual-override toggle is on).
///   2. Ask the backend to provision a NetBird group + setup key. The
///      backend returns:
///         - groupId       — used as the lobby's "network id" for
///                           joiners to identify this lobby
///         - setupKey      — secret for our local agent to enroll
///         - managementUrl — URL of the NetBird management server
///   3. Run "netbird up --management-url … --setup-key …" so the local
///      agent joins the lobby's group.
///   4. Wait for NetBird to give us an assigned IP.
///   5. Publish the lobby to the directory with that IP + BCA's port
///      (default 7777).
///
/// Joiners go through <see cref="JoinLobbyAsync"/>: they fetch the
/// setup key from the backend, run "netbird up", get an IP, and the
/// modal shows them the host's IP to paste into BCA.
///
/// When the user stops hosting, we DELETE the NetBird resources on the
/// backend (which cleans up group/key/policy via the Management API)
/// and run "netbird down" locally.
/// </summary>
public sealed class LobbyHostingService : IDisposable
{
    readonly AppSettings _settings;
    readonly NetBirdController _nb;
    readonly LobbyPublisherService _publisher;
    readonly HttpClient _http;

    enum State { Idle, Provisioning, Advertising, Failed }
    State _state = State.Idle;
    DateTime _lastAttempt = DateTime.MinValue;

    string _currentGroupId = "";
    string _currentVirtualIP = "";

    LobbyData? _lastSeenLobby;
    string _lastSeenHostName = "";

    // ── Match-transition grace period ───────────────────────────────────
    //
    // When BCA starts loading a match, its in-memory LobbyGameState gets
    // disposed and our snapshot reader returns lobby=null for the
    // duration of the match. Without a grace period we'd tear down the
    // NetBird group at exactly the moment the players need the VPN
    // tunnel to be working. Instead, we remember that we were hosting,
    // mark the lobby as "transient missing", and only actually tear
    // down if (a) the user disables advertising, (b) the user goes
    // back to the main menu, or (c) the lobby data hasn't reappeared
    // for a long time (well past the longest match length).
    //
    // When the match ends and BCA pops the lobby back up, we keep the
    // existing group/policy/setup-key in place — the players are still
    // in the same NetBird network from match to match, no reconnect
    // needed.

    DateTime _lobbyMissingSince = DateTime.MinValue;
    bool _suspectInMatch;
    /// <summary>Hold the NetBird tunnel alive this long after the lobby
    /// data disappears, to cover match-load + the match itself.
    /// Longer than the longest BCA match (Q-Ball can go ~15 min).</summary>
    static readonly TimeSpan MatchHoldGrace = TimeSpan.FromMinutes(25);

    public bool IsAdvertising => _state == State.Advertising;
    public string StatusText { get; private set; } = "Idle";
    public string CurrentGroupId => _currentGroupId;
    public string CurrentVirtualIP => _currentVirtualIP;
    public string ExternalEndpoint =>
        string.IsNullOrEmpty(_currentVirtualIP) ? "" : $"{_currentVirtualIP}:{BcaPort}";

    // ── Joined-as-non-host state ────────────────────────────────────────
    //
    // When the user clicks "Join" on someone else's lobby, we enroll
    // the local NetBird agent into that lobby's group. The agent stays
    // connected until the user explicitly leaves (or the tracker
    // exits). These fields drive the "Connected to: X" banner so the
    // user can disconnect cleanly.

    public string JoinedLobbyName    { get; private set; } = "";
    public string JoinedHostName     { get; private set; } = "";
    public string JoinedConnectString { get; private set; } = "";
    public string JoinedGroupId      { get; private set; } = "";
    public bool   IsJoined           => !string.IsNullOrEmpty(JoinedGroupId);

    /// <summary>BCA's default direct-connect port.</summary>
    const int BcaPort = 7777;

    public LobbyHostingService(AppSettings settings)
    {
        _settings  = settings;
        _nb        = new NetBirdController();
        _publisher = new LobbyPublisherService();
        _publisher.SetEndpoint(settings.DataSubmissionEndpoint ?? "");
        _http      = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public LobbyPublisherService Publisher => _publisher;
    public NetBirdController NetBird => _nb;

    public void ApplyConfig() =>
        _publisher.SetEndpoint(_settings.DataSubmissionEndpoint ?? "");

    string _lastTickLine = "";
    DateTime _lastTickLogTime = DateTime.MinValue;

    public void OnSnapshot(LobbyData? lobby, string localHostName)
        => OnSnapshot(lobby, localHostName, inMatch: false, isMainMenu: false);

    /// <summary>
    /// Drive the host state machine from the next memory snapshot.
    ///
    /// `inMatch` and `isMainMenu` come from the same snapshot the lobby
    /// data came from. They let us distinguish "lobby is gone because
    /// the match started" (keep the NetBird tunnel alive — the players
    /// need it!) from "lobby is gone because the user backed out to the
    /// main menu" (tear down promptly).
    /// </summary>
    public void OnSnapshot(LobbyData? lobby, string localHostName,
                           bool inMatch, bool isMainMenu)
    {
        if (lobby is not null)
        {
            string line =
                $"[LobbyHost] tick: map={lobby.MapName} mode={lobby.ModeName} " +
                $"players={lobby.CurrentPlayerCount}/{lobby.MaxTeamSize * 2} " +
                $"isHost={lobby.LocalPlayerIsHost} " +
                $"advertise={_settings.LobbyAdvertisingEnabled} state={_state} " +
                $"inMatch={inMatch} isMainMenu={isMainMenu} " +
                $"status=\"{StatusText}\" " +
                $"groupId={(string.IsNullOrEmpty(_currentGroupId) ? "-" : _currentGroupId)} " +
                $"vip={(string.IsNullOrEmpty(_currentVirtualIP) ? "-" : _currentVirtualIP)}";
            // Suppress repeats: identical line within 30 s is dropped.
            // Anything that materially changed (state, status, IP,
            // player count, map) generates a new line immediately.
            bool changed = line != _lastTickLine;
            bool dueAnyway = DateTime.UtcNow - _lastTickLogTime > TimeSpan.FromSeconds(30);
            if (changed || dueAnyway)
            {
                DiagLog.Write(line);
                _lastTickLine = line;
                _lastTickLogTime = DateTime.UtcNow;
            }
        }
        else if (_state != State.Idle)
        {
            // No lobby data, but the state machine is active — log
            // a brief tick so we can see what's going on (e.g. during
            // a match, when we're intentionally holding the tunnel).
            string line =
                $"[LobbyHost] tick: lobby=null " +
                $"advertise={_settings.LobbyAdvertisingEnabled} state={_state} " +
                $"inMatch={inMatch} isMainMenu={isMainMenu} " +
                $"status=\"{StatusText}\" " +
                $"groupId={(string.IsNullOrEmpty(_currentGroupId) ? "-" : _currentGroupId)} " +
                $"vip={(string.IsNullOrEmpty(_currentVirtualIP) ? "-" : _currentVirtualIP)}";
            bool changed = line != _lastTickLine;
            bool dueAnyway = DateTime.UtcNow - _lastTickLogTime > TimeSpan.FromSeconds(30);
            if (changed || dueAnyway)
            {
                DiagLog.Write(line);
                _lastTickLine = line;
                _lastTickLogTime = DateTime.UtcNow;
            }
        }

        if (!_settings.LobbyAdvertisingEnabled)
        {
            if (_state != State.Idle) _ = TearDown("advertising disabled");
            return;
        }

        bool isHost = lobby is not null && (lobby.LocalPlayerIsHost || _settings.LobbyForceHost);

        // Lobby data present + we're host → normal path. Clear any
        // grace-period state and proceed.
        if (lobby is not null && isHost)
        {
            if (_lobbyMissingSince != DateTime.MinValue)
            {
                DiagLog.Write($"[LobbyHost] lobby data reappeared after {(DateTime.UtcNow - _lobbyMissingSince).TotalSeconds:F1}s — clearing match-hold grace");
                _lobbyMissingSince = DateTime.MinValue;
                _suspectInMatch = false;
            }
        }
        else
        {
            // Lobby data is missing (or we're not host). Decide whether
            // to tear down immediately or hold the tunnel alive.

            // Quick rejection: we never started hosting → nothing to do.
            if (_state == State.Idle)
                return;

            // If BCA reports we're back at the main menu, the user
            // genuinely left the lobby — tear down immediately.
            if (isMainMenu)
            {
                _ = TearDown("returned to main menu");
                return;
            }

            // Note the first time we noticed the lobby went missing.
            if (_lobbyMissingSince == DateTime.MinValue)
            {
                _lobbyMissingSince = DateTime.UtcNow;
                _suspectInMatch = inMatch;
                DiagLog.Write($"[LobbyHost] lobby data gone (inMatch={inMatch}) — entering match-hold grace, keeping NetBird up");
            }
            else if (inMatch && !_suspectInMatch)
            {
                // Match started after the lobby vanished — confirms
                // the disappearance was a match transition, not a
                // user leaving.
                _suspectInMatch = true;
                DiagLog.Write("[LobbyHost] match started — holding NetBird tunnel alive for the duration");
            }

            // Hard limit: if the lobby has been gone for longer than a
            // realistic match could last, give up. Covers the case
            // where the user alt-tabs and force-closes BCA without
            // exiting cleanly.
            var gone = DateTime.UtcNow - _lobbyMissingSince;
            if (gone > MatchHoldGrace)
            {
                _ = TearDown($"lobby data gone for {gone.TotalMinutes:F1}m (>{MatchHoldGrace.TotalMinutes:F0}m grace)");
                return;
            }

            // While in the grace window, keep state as-is. Don't
            // tear down, don't retry, just hold the tunnel.
            if (_state == State.Advertising)
                StatusText = inMatch
                    ? $"In match — VPN active at {ExternalEndpoint}"
                    : $"Lobby paused — holding VPN at {ExternalEndpoint}";
            return;
        }

        _lastSeenLobby    = lobby;
        // Prefer the scoreboard's local player name (set during matches),
        // but fall back to the lobby PlayerState's name when the
        // scoreboard isn't populated yet (in-lobby pre-match).
        _lastSeenHostName = !string.IsNullOrEmpty(localHostName)
            ? localHostName
            : (lobby!.LocalPlayerName ?? "");

        switch (_state)
        {
            case State.Idle:
                _ = StartHostingAsync();
                break;

            case State.Provisioning:
                break;

            case State.Advertising:
                _publisher.UpdateSnapshot(BuildInfo(lobby!, localHostName));
                StatusText = $"Hosting at {ExternalEndpoint}";
                MaybeSendHostMemberHeartbeat(lobby!, localHostName);
                break;

            case State.Failed:
                if (DateTime.UtcNow - _lastAttempt > TimeSpan.FromSeconds(60))
                    _ = StartHostingAsync();
                break;
        }
    }

    int _attemptCounter;

    async Task StartHostingAsync()
    {
        int attempt = ++_attemptCounter;
        var attemptStart = DateTime.UtcNow;
        DiagLog.Write($"[LobbyHost] StartHostingAsync #{attempt}: entry");

        _state = State.Provisioning;
        _lastAttempt = DateTime.UtcNow;
        StatusText = "Preparing NetBird…";

        if (!_nb.IsInstalled)
        {
            _state = State.Failed;
            StatusText = "NetBird isn't installed. Download from https://pkgs.netbird.io/windows/x64 and install it.";
            DiagLog.Write($"[LobbyHost] #{attempt} FAIL@install: {StatusText}");
            return;
        }
        DiagLog.Write($"[LobbyHost] #{attempt} netbird CLI at {_nb.CliPath}");

        string endpoint = _settings.DataSubmissionEndpoint?.TrimEnd('/') ?? "";
        if (string.IsNullOrEmpty(endpoint))
        {
            _state = State.Failed;
            StatusText = "Server endpoint not set in Settings.";
            DiagLog.Write($"[LobbyHost] #{attempt} FAIL@endpoint: {StatusText}");
            return;
        }

        // Ask the backend to provision the lobby's NetBird resources.
        StatusText = "Creating lobby network…";
        string hostProfileId = !string.IsNullOrEmpty(_lastSeenLobby?.LocalPlayerProfileId)
            ? _lastSeenLobby.LocalPlayerProfileId
            : _settings.AnonymousAccountId;
        DiagLog.Write($"[LobbyHost] #{attempt} POST {endpoint}/v1/nb/lobbies hostProfileId={hostProfileId}");
        var t0 = DateTime.UtcNow;
        var lobbyRes = await CreateLobbyAsync(endpoint, hostProfileId);
        DiagLog.Write($"[LobbyHost] #{attempt} CreateLobbyAsync took {(DateTime.UtcNow - t0).TotalMilliseconds:F0}ms result={(lobbyRes is null ? "null" : "ok")}");
        if (lobbyRes is null)
        {
            _state = State.Failed;
            StatusText = "Failed to create lobby on the server.";
            DiagLog.Write($"[LobbyHost] #{attempt} FAIL@create: {StatusText}");
            return;
        }

        _currentGroupId = lobbyRes.GroupId;
        DiagLog.Write($"[LobbyHost] #{attempt} got groupId={lobbyRes.GroupId} mgmt={lobbyRes.ManagementUrl} keyLen={lobbyRes.SetupKey?.Length ?? 0}");

        // Enroll the local agent.
        StatusText = "Joining lobby network…";
        var t1 = DateTime.UtcNow;
        DiagLog.Write($"[LobbyHost] #{attempt} netbird up…");
        if (!await _nb.UpAsync(lobbyRes.ManagementUrl, lobbyRes.SetupKey ?? ""))
        {
            _state = State.Failed;
            StatusText = "NetBird enrollment failed.";
            DiagLog.Write($"[LobbyHost] #{attempt} FAIL@up: netbird up returned false (took {(DateTime.UtcNow - t1).TotalMilliseconds:F0}ms) — see [NB] stderr lines above");
            return;
        }
        DiagLog.Write($"[LobbyHost] #{attempt} netbird up ok ({(DateTime.UtcNow - t1).TotalMilliseconds:F0}ms)");

        // Wait for IP assignment.
        StatusText = "Waiting for IP assignment…";
        var t2 = DateTime.UtcNow;
        DiagLog.Write($"[LobbyHost] #{attempt} waiting for NetBird IP (up to 30s)…");
        string ip = await _nb.WaitForAssignedIPAsync(30);
        if (string.IsNullOrEmpty(ip))
        {
            _state = State.Failed;
            StatusText = "NetBird didn't assign us an IP in time.";
            DiagLog.Write($"[LobbyHost] #{attempt} FAIL@waitip: no IP after {(DateTime.UtcNow - t2).TotalSeconds:F1}s");
            return;
        }
        _currentVirtualIP = ip;
        DiagLog.Write($"[LobbyHost] #{attempt} IP assigned: {ip} (took {(DateTime.UtcNow - t2).TotalMilliseconds:F0}ms)");

        if (_lastSeenLobby is null)
        {
            _state = State.Failed;
            StatusText = "Lobby data vanished during setup";
            DiagLog.Write($"[LobbyHost] #{attempt} FAIL@lobby-gone: {StatusText}");
            return;
        }

        _publisher.StartHosting(BuildInfo(_lastSeenLobby, _lastSeenHostName));
        _state = State.Advertising;
        StatusText = $"Hosting at {ExternalEndpoint}";
        DiagLog.Write($"[LobbyHost] #{attempt} ADVERTISING at {ExternalEndpoint} (total {(DateTime.UtcNow - attemptStart).TotalMilliseconds:F0}ms)");
    }

    LobbyInfo BuildInfo(LobbyData lobby, string hostName)
    {
        // Prefer the lobby's just-read PlayerNamePrivate; the
        // scoreboard-derived hostName is empty pre-match.
        string effectiveHost = !string.IsNullOrEmpty(lobby.LocalPlayerName)
            ? lobby.LocalPlayerName
            : (string.IsNullOrEmpty(hostName) ? "Player" : hostName);

        return new()
        {
            HostProfileId      = string.IsNullOrEmpty(lobby.LocalPlayerProfileId)
                                    ? _settings.AnonymousAccountId
                                    : lobby.LocalPlayerProfileId,
            HostName           = effectiveHost,
            LobbyName          = !string.IsNullOrWhiteSpace(_settings.LobbyAdvertisedName)
                                    ? _settings.LobbyAdvertisedName
                                    : $"{effectiveHost}'s lobby",
            MapRowName         = lobby.MapName,
            GameModeRowName    = lobby.ModeName,
            MaxTeamSize        = lobby.MaxTeamSize,
            CurrentPlayerCount = lobby.CurrentPlayerCount,
            HasPassword        = lobby.HasPassword,
            HostExternalIP     = _currentVirtualIP,
            HostExternalPort   = BcaPort,
            NetBirdGroupId     = _currentGroupId,
            LocalPlayerIsHost  = true,
        };
    }

    async Task TearDown(string reason)
    {
        DiagLog.Write($"[LobbyHost] Tearing down: {reason}");
        StatusText = "Stopped: " + reason;
        await _publisher.StopHosting();

        if (!string.IsNullOrEmpty(_currentGroupId))
        {
            string endpoint = _settings.DataSubmissionEndpoint?.TrimEnd('/') ?? "";
            if (!string.IsNullOrEmpty(endpoint))
            {
                try
                {
                    await _http.DeleteAsync($"{endpoint}/v1/nb/lobbies/{_currentGroupId}");
                }
                catch (Exception ex)
                {
                    DiagLog.Write($"[LobbyHost] NB delete failed: {ex.Message}");
                }
            }
            try { await _nb.DownAsync(); } catch { }
        }

        _currentGroupId   = "";
        _currentVirtualIP = "";
        _state = State.Idle;
    }

    /// <summary>
    /// Called by the Lobby Browser when a user clicks a lobby to join.
    /// Returns the host's virtual IP they should paste into BCA, or
    /// empty on failure.
    /// </summary>
    public async Task<string> JoinLobbyAsync(LobbyInfo lobby, CancellationToken ct = default)
    {
        DiagLog.Write($"[LobbyHost] JoinLobbyAsync: groupId={lobby.NetBirdGroupId} host={lobby.HostName} ip={lobby.HostExternalIP}");

        if (!_nb.IsInstalled)
        {
            DiagLog.Write("[LobbyHost] Join: NetBird not installed");
            return "";
        }
        if (string.IsNullOrEmpty(lobby.NetBirdGroupId))
        {
            DiagLog.Write("[LobbyHost] Join: lobby has no NetBird group id");
            return "";
        }

        string endpoint = _settings.DataSubmissionEndpoint?.TrimEnd('/') ?? "";
        if (string.IsNullOrEmpty(endpoint))
        {
            DiagLog.Write("[LobbyHost] Join: no server endpoint configured");
            return "";
        }

        // Fetch the setup key from the backend.
        JoinResponse? joinRes;
        try
        {
            DiagLog.Write($"[LobbyHost] Join: POST {endpoint}/v1/nb/lobbies/{lobby.NetBirdGroupId}/join");
            using var resp = await _http.PostAsync(
                $"{endpoint}/v1/nb/lobbies/{lobby.NetBirdGroupId}/join",
                content: null, cancellationToken: ct);
            if (!resp.IsSuccessStatusCode)
            {
                string respBody = "";
                try
                {
                    respBody = await resp.Content.ReadAsStringAsync(ct);
                    if (respBody.Length > 512) respBody = respBody.Substring(0, 512) + "…";
                }
                catch { }
                DiagLog.Write($"[LobbyHost] Join HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {respBody}");
                return "";
            }
            joinRes = await resp.Content.ReadFromJsonAsync<JoinResponse>(cancellationToken: ct);
        }
        catch (Exception ex)
        {
            DiagLog.Write($"[LobbyHost] Join failed: {ex.GetType().Name}: {ex.Message}");
            return "";
        }
        if (joinRes is null || string.IsNullOrEmpty(joinRes.SetupKey))
        {
            DiagLog.Write("[LobbyHost] Join: backend returned empty setup key");
            return "";
        }
        DiagLog.Write($"[LobbyHost] Join: got setup key (len={joinRes.SetupKey.Length}) mgmt={joinRes.ManagementUrl}");

        // Enroll locally.
        DiagLog.Write("[LobbyHost] Join: netbird up…");
        if (!await _nb.UpAsync(joinRes.ManagementUrl, joinRes.SetupKey, ct))
        {
            DiagLog.Write("[LobbyHost] Join: netbird up failed");
            return "";
        }
        DiagLog.Write("[LobbyHost] Join: netbird up ok");

        // Remember the connection so the UI can show a "connected to" banner
        // and offer a Leave button. This is a non-host membership; it
        // doesn't interact with the host state machine above.
        JoinedGroupId       = lobby.NetBirdGroupId;
        JoinedLobbyName     = lobby.LobbyName;
        JoinedHostName      = lobby.HostName;
        JoinedConnectString = $"{lobby.HostExternalIP}:{lobby.HostExternalPort}";

        // Start the periodic /members heartbeat so we appear in the
        // lobby's "Connected players" card for everyone else (and so
        // our own card has someone to show).
        StartJoinedHeartbeatLoop();

        // The host's IP is in the lobby payload — we don't need to
        // discover anything. The user pastes that into BCA.
        return lobby.HostExternalIP;
    }

    /// <summary>
    /// Disconnect from a previously joined lobby's NetBird network.
    /// Called when the user clicks "Leave" in the joined banner.
    /// Best-effort: still clears local state even if the netbird-down
    /// call fails.
    /// </summary>
    public async Task LeaveJoinedLobbyAsync(CancellationToken ct = default)
    {
        DiagLog.Write("[LobbyHost] Leaving joined lobby");
        try { await _nb.DownAsync(ct); } catch { }
        JoinedGroupId       = "";
        JoinedLobbyName     = "";
        JoinedHostName      = "";
        JoinedConnectString = "";
        _lastJoinedMemberHeartbeat = DateTime.MinValue;
    }

    // ── Backend client ──────────────────────────────────────────────────

    async Task<CreateLobbyResponse?> CreateLobbyAsync(string endpoint, string hostProfileId)
    {
        try
        {
            var body = new { hostProfileId };
            using var resp = await _http.PostAsJsonAsync($"{endpoint}/v1/nb/lobbies", body);
            if (!resp.IsSuccessStatusCode)
            {
                string respBody = "";
                try
                {
                    respBody = await resp.Content.ReadAsStringAsync();
                    if (respBody.Length > 512) respBody = respBody.Substring(0, 512) + "…";
                }
                catch { }
                DiagLog.Write($"[LobbyHost] Create lobby HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {respBody}");
                return null;
            }
            return await resp.Content.ReadFromJsonAsync<CreateLobbyResponse>();
        }
        catch (TaskCanceledException ex)
        {
            DiagLog.Write($"[LobbyHost] Create lobby timed out after {_http.Timeout.TotalSeconds:F0}s: {ex.Message}");
            return null;
        }
        catch (HttpRequestException ex)
        {
            DiagLog.Write($"[LobbyHost] Create lobby HTTP error: {ex.Message} (inner: {ex.InnerException?.Message ?? "none"})");
            return null;
        }
        catch (Exception ex)
        {
            DiagLog.Write($"[LobbyHost] Create lobby failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    sealed class CreateLobbyResponse
    {
        [JsonPropertyName("groupId")]       public string GroupId       { get; set; } = "";
        [JsonPropertyName("setupKey")]      public string SetupKey      { get; set; } = "";
        [JsonPropertyName("managementUrl")] public string ManagementUrl { get; set; } = "";
    }

    sealed class JoinResponse
    {
        [JsonPropertyName("setupKey")]      public string SetupKey      { get; set; } = "";
        [JsonPropertyName("managementUrl")] public string ManagementUrl { get; set; } = "";
    }

    public sealed class LobbyMember
    {
        [JsonPropertyName("name")]         public string Name      { get; set; } = "";
        [JsonPropertyName("profileId")]    public string ProfileId { get; set; } = "";
        [JsonPropertyName("netbirdIp")]    public string NetBirdIP { get; set; } = "";
        [JsonPropertyName("isHost")]       public bool   IsHost    { get; set; }
        [JsonPropertyName("joinedAtUnix")] public long   JoinedAtUnix { get; set; }
    }

    sealed class MembersResponse
    {
        [JsonPropertyName("members")]
        public List<LobbyMember> Members { get; set; } = new();
    }

    /// <summary>
    /// POST our own (name, NetBird IP, host-flag) to the backend so
    /// everyone else in the lobby can see us in their "Connected
    /// players" card. Called once on join/host-start, then every ~30s
    /// as a heartbeat so the server can reap stale entries.
    /// </summary>
    async Task PostMemberHeartbeatAsync(string groupId, string netbirdIp,
                                        string name, string profileId, bool isHost,
                                        CancellationToken ct = default)
    {
        string endpoint = _settings.DataSubmissionEndpoint?.TrimEnd('/') ?? "";
        if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(groupId) || string.IsNullOrEmpty(netbirdIp))
            return;
        try
        {
            var body = new
            {
                name      = name      ?? "",
                profileId = profileId ?? "",
                netbirdIp,
                isHost
            };
            using var resp = await _http.PostAsJsonAsync(
                $"{endpoint}/v1/nb/lobbies/{groupId}/members", body, ct);
            if (!resp.IsSuccessStatusCode)
                DiagLog.Write($"[LobbyHost] member heartbeat HTTP {(int)resp.StatusCode} for group={groupId}");
        }
        catch (Exception ex)
        {
            DiagLog.Write($"[LobbyHost] member heartbeat failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// GET the current member list for a lobby. Used by the
    /// "Connected players" UI card. Returns an empty list on any
    /// error so the UI just shows nothing instead of crashing.
    /// </summary>
    public async Task<List<LobbyMember>> GetLobbyMembersAsync(string groupId, CancellationToken ct = default)
    {
        string endpoint = _settings.DataSubmissionEndpoint?.TrimEnd('/') ?? "";
        if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(groupId))
            return new List<LobbyMember>();
        try
        {
            using var resp = await _http.GetAsync(
                $"{endpoint}/v1/nb/lobbies/{groupId}/members", ct);
            if (!resp.IsSuccessStatusCode)
                return new List<LobbyMember>();
            var parsed = await resp.Content.ReadFromJsonAsync<MembersResponse>(cancellationToken: ct);
            return parsed?.Members ?? new List<LobbyMember>();
        }
        catch
        {
            return new List<LobbyMember>();
        }
    }

    /// <summary>
    /// What ID we report as our membership "profileId" to the
    /// backend. Prefers the BCA-extracted CustomProfileID, falls
    /// back to the anonymous account id so we still appear in the
    /// member list even before BCA has populated PlayerState.
    /// </summary>
    string GetReportableProfileId()
        => !string.IsNullOrEmpty(_lastSeenLobby?.LocalPlayerProfileId)
            ? _lastSeenLobby.LocalPlayerProfileId
            : (_settings.AnonymousAccountId ?? "");

    // ── Member heartbeats ────────────────────────────────────────────────
    //
    // Both the host and any joiner POST themselves to /members every ~30s.
    // The server uses LastSeen to evict stale entries (>2 min). The
    // first POST returns immediately; subsequent ones are rate-limited
    // by these timestamps so we don't hammer the backend from the
    // 500ms snapshot tick.

    DateTime _lastHostMemberHeartbeat = DateTime.MinValue;
    DateTime _lastJoinedMemberHeartbeat = DateTime.MinValue;
    static readonly TimeSpan MemberHeartbeatInterval = TimeSpan.FromSeconds(30);

    void MaybeSendHostMemberHeartbeat(LobbyData lobby, string localHostName)
    {
        if (string.IsNullOrEmpty(_currentGroupId) || string.IsNullOrEmpty(_currentVirtualIP))
            return;
        if (DateTime.UtcNow - _lastHostMemberHeartbeat < MemberHeartbeatInterval)
            return;
        _lastHostMemberHeartbeat = DateTime.UtcNow;
        string name = !string.IsNullOrEmpty(localHostName)
            ? localHostName
            : (lobby.LocalPlayerName ?? "");
        _ = PostMemberHeartbeatAsync(_currentGroupId, _currentVirtualIP,
                                     name, GetReportableProfileId(),
                                     isHost: true);
    }

    /// <summary>
    /// Schedule periodic member heartbeats for the joined-lobby case.
    /// Called once right after JoinLobbyAsync succeeds; loops until
    /// LeaveJoinedLobby clears JoinedGroupId.
    /// </summary>
    void StartJoinedHeartbeatLoop()
    {
        _ = Task.Run(async () =>
        {
            while (!string.IsNullOrEmpty(JoinedGroupId))
            {
                try
                {
                    var status = await _nb.GetStatusAsync();
                    if (!string.IsNullOrEmpty(status.IP))
                    {
                        await PostMemberHeartbeatAsync(JoinedGroupId, status.IP,
                            name: !string.IsNullOrEmpty(_lastSeenLobby?.LocalPlayerName)
                                ? _lastSeenLobby.LocalPlayerName
                                : (JoinedHostName ?? ""),
                            profileId: GetReportableProfileId(),
                            isHost: false);
                        _lastJoinedMemberHeartbeat = DateTime.UtcNow;
                    }
                }
                catch (Exception ex)
                {
                    DiagLog.Write($"[LobbyHost] joined heartbeat threw: {ex.GetType().Name}: {ex.Message}");
                }
                try { await Task.Delay(MemberHeartbeatInterval); }
                catch { break; }
            }
        });
    }

    public void Dispose()
    {
        try { _ = _publisher.StopHosting(); } catch { }
        if (IsJoined)
        {
            try { LeaveJoinedLobbyAsync().GetAwaiter().GetResult(); } catch { }
        }
        _publisher.Dispose();
        _http.Dispose();
    }
}
