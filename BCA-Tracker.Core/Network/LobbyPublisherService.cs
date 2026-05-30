using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace BCATracker.Core
{
    /// <summary>
    /// Publishes the host's lobby to the directory backend on a heartbeat,
    /// and fetches the list of advertised lobbies for the browser.
    ///
    /// Lifecycle:
    /// <list type="bullet">
    ///   <item>Constructed once at app start, given an endpoint URL.</item>
    ///   <item>UI calls <see cref="StartHosting"/> when the host toggles
    ///   the "advertise" option and we have a valid UPnP mapping.</item>
    ///   <item>UI calls <see cref="StopHosting"/> on toggle-off, app exit,
    ///   or when memory says we left the lobby.</item>
    ///   <item>Browser calls <see cref="FetchLobbiesAsync"/> on page load
    ///   and on the user's refresh tap.</item>
    /// </list>
    ///
    /// Heartbeat interval is 30s — long enough not to flood the backend,
    /// short enough that stale lobbies fall off quickly (backend should
    /// expire entries after ~2 minutes of no heartbeat).
    /// </summary>
    public sealed class LobbyPublisherService : IDisposable
    {
        readonly HttpClient _http;
        readonly Timer _heartbeat;
        readonly object _lock = new();

        string _endpoint = "";
        LobbyInfo? _current;
        bool _disposed;

        public bool IsHosting { get; private set; }
        public string LastError { get; private set; } = "";
        public DateTime LastPostUtc { get; private set; } = DateTime.MinValue;

        public LobbyPublisherService()
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            _heartbeat = new Timer(_ => _ = HeartbeatAsync(), null,
                Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>Set/update the backend base URL (from settings). Empty
        /// means the publisher does nothing.</summary>
        public void SetEndpoint(string endpoint)
        {
            lock (_lock) _endpoint = (endpoint ?? "").TrimEnd('/');
        }

        /// <summary>Begin advertising the given lobby. Called by the UI
        /// once UPnP succeeded and the host toggled "advertise" on.</summary>
        public void StartHosting(LobbyInfo info)
        {
            lock (_lock)
            {
                _current = info;
                IsHosting = true;
            }
            _heartbeat.Change(TimeSpan.Zero, TimeSpan.FromSeconds(30));
            DiagLog.Write($"[LobbyPub] Started hosting '{info.LobbyName}' " +
                          $"on {info.HostExternalIP}:{info.HostExternalPort}");
        }

        /// <summary>Update the snapshot the heartbeat sends (e.g. player
        /// count changed). Doesn't reset the timer — next heartbeat picks
        /// up the new data.</summary>
        public void UpdateSnapshot(LobbyInfo info)
        {
            lock (_lock) _current = info;
        }

        /// <summary>Stop advertising. Sends one final DELETE so the lobby
        /// disappears from the browser immediately instead of waiting for
        /// the backend to time it out.</summary>
        public async Task StopHosting()
        {
            LobbyInfo? snapshot;
            string endpoint;
            lock (_lock)
            {
                snapshot = _current;
                endpoint = _endpoint;
                _current = null;
                IsHosting = false;
            }
            _heartbeat.Change(Timeout.Infinite, Timeout.Infinite);

            if (snapshot is not null && !string.IsNullOrEmpty(endpoint))
            {
                try
                {
                    string url = $"{endpoint}/v1/lobbies/{snapshot.HostProfileId}";
                    await _http.DeleteAsync(url).ConfigureAwait(false);
                    DiagLog.Write("[LobbyPub] Posted goodbye");
                }
                catch (Exception ex)
                {
                    DiagLog.Write($"[LobbyPub] Goodbye failed: {ex.Message}");
                }
            }
        }

        async Task HeartbeatAsync()
        {
            LobbyInfo? snapshot;
            string endpoint;
            lock (_lock)
            {
                snapshot = _current;
                endpoint = _endpoint;
            }
            if (snapshot is null || string.IsNullOrEmpty(endpoint)) return;
            if (string.IsNullOrEmpty(snapshot.HostExternalIP)) return;

            try
            {
                snapshot.CapturedAtUtc = DateTime.UtcNow;
                var body = new LobbyPostBody
                {
                    HostProfileId      = snapshot.HostProfileId,
                    HostName           = snapshot.HostName,
                    LobbyName          = snapshot.LobbyName,
                    MapRowName         = snapshot.MapRowName,
                    GameModeRowName    = snapshot.GameModeRowName,
                    MaxTeamSize        = snapshot.MaxTeamSize,
                    CurrentPlayerCount = snapshot.CurrentPlayerCount,
                    HasPassword        = snapshot.HasPassword,
                    Hidden             = snapshot.Hidden,
                    HostExternalIP     = snapshot.HostExternalIP,
                    HostExternalPort   = snapshot.HostExternalPort,
                    NetBirdGroupId     = snapshot.NetBirdGroupId,
                    Password           = string.IsNullOrEmpty(snapshot.Password) ? null : snapshot.Password,
                };
                using var resp = await _http.PostAsJsonAsync(
                    $"{endpoint}/v1/lobbies", body).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    LastPostUtc = DateTime.UtcNow;
                    LastError = "";
                }
                else
                {
                    LastError = $"HTTP {(int)resp.StatusCode}";
                    DiagLog.Write($"[LobbyPub] Heartbeat returned {(int)resp.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                DiagLog.Write($"[LobbyPub] Heartbeat failed: {ex.Message}");
            }
        }

        /// <summary>Fetch the current list of advertised lobbies. Returns
        /// an empty list on any failure (the UI shows that as "no
        /// lobbies").</summary>
        public async Task<List<LobbyInfo>> FetchLobbiesAsync(CancellationToken ct = default)
        {
            string endpoint;
            lock (_lock) endpoint = _endpoint;
            if (string.IsNullOrEmpty(endpoint)) return new();

            try
            {
                var rows = await _http.GetFromJsonAsync<List<LobbyPostBody>>(
                    $"{endpoint}/v1/lobbies", ct).ConfigureAwait(false);
                if (rows is null) return new();
                var result = new List<LobbyInfo>(rows.Count);
                foreach (var r in rows)
                {
                    result.Add(new LobbyInfo
                    {
                        HostProfileId      = r.HostProfileId      ?? "",
                        HostName           = r.HostName           ?? "",
                        LobbyName          = r.LobbyName          ?? "",
                        MapRowName         = r.MapRowName         ?? "",
                        GameModeRowName    = r.GameModeRowName    ?? "",
                        MaxTeamSize        = r.MaxTeamSize,
                        CurrentPlayerCount = r.CurrentPlayerCount,
                        HasPassword        = r.HasPassword,
                        Hidden             = r.Hidden,
                        HostExternalIP     = r.HostExternalIP     ?? "",
                        HostExternalPort   = r.HostExternalPort,
                        NetBirdGroupId     = r.NetBirdGroupId     ?? "",
                    });
                }
                return result;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                DiagLog.Write($"[LobbyPub] Fetch failed: {ex.Message}");
                return new();
            }
        }

        /// <summary>
        /// Fetch a single lobby by its NetBird group id. Used by the
        /// "Join by ID" flow to find hidden lobbies that the public
        /// list doesn't return. Returns null if not found.
        /// </summary>
        public async Task<LobbyInfo?> FetchByGroupIdAsync(string endpoint, string groupId, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(groupId)) return null;
            endpoint = endpoint.TrimEnd('/');
            try
            {
                using var resp = await _http.GetAsync(
                    $"{endpoint}/v1/lobbies/by-group/{groupId}", ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;
                var r = await resp.Content.ReadFromJsonAsync<LobbyPostBody>(cancellationToken: ct).ConfigureAwait(false);
                if (r is null) return null;
                return new LobbyInfo
                {
                    HostProfileId      = r.HostProfileId      ?? "",
                    HostName           = r.HostName           ?? "",
                    LobbyName          = r.LobbyName          ?? "",
                    MapRowName         = r.MapRowName         ?? "",
                    GameModeRowName    = r.GameModeRowName    ?? "",
                    MaxTeamSize        = r.MaxTeamSize,
                    CurrentPlayerCount = r.CurrentPlayerCount,
                    HasPassword        = r.HasPassword,
                    Hidden             = r.Hidden,
                    HostExternalIP     = r.HostExternalIP     ?? "",
                    HostExternalPort   = r.HostExternalPort,
                    NetBirdGroupId     = r.NetBirdGroupId     ?? "",
                };
            }
            catch (Exception ex)
            {
                DiagLog.Write($"[LobbyPub] FetchByGroupId failed: {ex.Message}");
                return null;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _heartbeat.Dispose(); } catch { }
            try { StopHosting().GetAwaiter().GetResult(); } catch { }
            _http.Dispose();
        }

        // ── Wire format (matches the backend contract) ──────────────────────
        sealed class LobbyPostBody
        {
            [JsonPropertyName("hostProfileId")]      public string? HostProfileId    { get; set; }
            [JsonPropertyName("hostName")]           public string? HostName         { get; set; }
            [JsonPropertyName("lobbyName")]          public string? LobbyName        { get; set; }
            [JsonPropertyName("mapRowName")]         public string? MapRowName       { get; set; }
            [JsonPropertyName("gameModeRowName")]    public string? GameModeRowName  { get; set; }
            [JsonPropertyName("maxTeamSize")]        public int     MaxTeamSize      { get; set; }
            [JsonPropertyName("currentPlayerCount")] public int     CurrentPlayerCount { get; set; }
            [JsonPropertyName("hasPassword")]        public bool    HasPassword      { get; set; }
            [JsonPropertyName("hidden")]             public bool    Hidden           { get; set; }
            [JsonPropertyName("hostExternalIP")]     public string? HostExternalIP   { get; set; }
            [JsonPropertyName("hostExternalPort")]   public int     HostExternalPort { get; set; }
            [JsonPropertyName("netBirdGroupId")]  public string? NetBirdGroupId { get; set; }

            /// <summary>Plaintext password. Sent only on first publish
            /// or when the host changes it; subsequent heartbeats omit
            /// this so the backend doesn't re-bcrypt on every tick.
            /// The JSON serializer drops null/empty values from the
            /// wire when this property is null.</summary>
            [JsonPropertyName("password")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string? Password { get; set; }
        }
    }
}
