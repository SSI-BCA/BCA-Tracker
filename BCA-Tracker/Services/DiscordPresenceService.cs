using System;
using BCATracker.Core;
using DiscordRPC;
using DiscordRPC.Logging;
using DiscordRPC.Message;

namespace BCATracker.UI.Services;

/// <summary>
/// Pushes Discord Rich Presence updates from <see cref="LiveMatchService"/>
/// events.
///
/// Lessons baked in:
///
/// 1. Discord rate-limits presence updates to ~1 every 15 s. We dedupe by
///    a "presence key" derived from the snapshot — same key, same presence,
///    don't push.
///
/// 2. The IPC pipe to Discord can drop (Discord client restarted, user
///    paused Discord, network glitch). The library has reconnect logic
///    built in but only fires it when SetPresence is called, AND only if
///    Initialize() ran successfully. We track the connected state via the
///    OnReady / OnConnectionFailed events and re-push on reconnect so the
///    presence isn't blank after a flap.
///
/// 3. Multiple tracker processes fight over the same Client ID. If we see
///    OnConnectionFailed repeatedly, we log it but don't respawn — Discord
///    will eventually pick the right pipe.
///
/// 4. We log lifecycle to diag.log so when the user reports "RPC went
///    blank" we can see if it was a connection drop vs a client-id problem
///    vs us never having pushed in the first place.
/// </summary>
public sealed class DiscordPresenceService : IDisposable
{
    DiscordRpcClient? _client;
    readonly LiveMatchService _live;
    DateTime _matchStartUtc;
    bool _initialized;
    bool _connected;

    /// <summary>The last presence we successfully pushed. Used to dedupe.</summary>
    string _lastKey = "";

    /// <summary>Wall-clock time of the last successful SetPresence call.
    /// We hard-throttle to ~2 s between sends — Discord rate-limits at
    /// 5/20s and we don't want any of our updates dropped server-side.</summary>
    DateTime _lastPushUtc = DateTime.MinValue;
    static readonly TimeSpan MinPushInterval = TimeSpan.FromSeconds(2);

    /// <summary>The last <see cref="RichPresence"/> we pushed. Resent on
    /// reconnect to recover the user's display after a connection flap.</summary>
    RichPresence? _lastPresence;

    public DiscordPresenceService(LiveMatchService live)
    {
        _live = live;
    }

    public bool IsConnected => _connected;

    public void Start(string clientId)
    {
        if (string.IsNullOrEmpty(clientId)) return;
        if (_initialized) return;

        try
        {
            DiagLog.Write($"[Discord] Initialise clientId={Mask(clientId)}");
            _client = new DiscordRpcClient(clientId)
            {
                Logger = new NullLogger(),
            };

            // Lifecycle wiring. The library invokes these on its IPC thread;
            // we don't touch UI controls here, just bookkeeping flags + log.
            _client.OnReady             += OnReady;
            _client.OnConnectionFailed  += OnConnectionFailed;
            _client.OnConnectionEstablished += OnConnectionEstablished;
            _client.OnClose             += OnClose;
            _client.OnError             += OnError;

            _client.Initialize();
            _initialized = true;

            _live.Tick         += OnTick;
            _live.MatchStarted += OnMatchStarted;
            _live.MatchEnded   += OnMatchEnded;
            _live.Detached     += OnDetached;

            // Default presence shown until a tick comes in. The push is
            // queued; if the IPC pipe isn't ready yet, the library buffers
            // it and sends as soon as OnReady fires.
            Push("idle:start", new RichPresence
            {
                Details = "Idle",
                State = "Battle Core Arena tracker running",
                Assets = new Assets { LargeImageKey = "logo", LargeImageText = "BCA-Tracker" },
            });
        }
        catch (Exception ex)
        {
            DiagLog.Exception("DiscordPresenceService.Start", ex);
            try { _client?.Dispose(); } catch { }
            _client = null;
            _initialized = false;
        }
    }

    public void Dispose()
    {
        try
        {
            _live.Tick         -= OnTick;
            _live.MatchStarted -= OnMatchStarted;
            _live.MatchEnded   -= OnMatchEnded;
            _live.Detached     -= OnDetached;
        }
        catch { }

        try { _client?.Deinitialize(); } catch { }
        try { _client?.Dispose(); } catch { }
        _client = null;
        _initialized = false;
        _connected = false;
    }

    // ── Discord lifecycle events ────────────────────────────────────────

    void OnReady(object? sender, ReadyMessage msg)
    {
        _connected = true;
        DiagLog.Write($"[Discord] OnReady — connected as {msg.User?.Username ?? "?"}");
        // Re-push the last presence so a reconnect doesn't leave the user
        // with a blank profile.
        if (_lastPresence is not null)
        {
            try { _client?.SetPresence(_lastPresence); } catch { }
        }
    }

    void OnConnectionEstablished(object? sender, ConnectionEstablishedMessage msg)
    {
        DiagLog.Write($"[Discord] OnConnectionEstablished pipe={msg.ConnectedPipe}");
    }

    void OnConnectionFailed(object? sender, ConnectionFailedMessage msg)
    {
        _connected = false;
        DiagLog.Write($"[Discord] OnConnectionFailed pipe={msg.FailedPipe} (Discord not running, or Client ID rejected)");
    }

    void OnClose(object? sender, CloseMessage msg)
    {
        _connected = false;
        DiagLog.Write($"[Discord] OnClose code={msg.Code} reason={msg.Reason}");
    }

    void OnError(object? sender, ErrorMessage msg)
    {
        DiagLog.Write($"[Discord] OnError code={msg.Code} message={msg.Message}");
    }

    // ── Snapshot → presence ─────────────────────────────────────────────

    void OnMatchStarted(MatchSnapshot snap)
    {
        _matchStartUtc = DateTime.UtcNow;
        UpdatePresence(snap);
    }

    void OnTick(MatchSnapshot snap) => UpdatePresence(snap);

    void OnMatchEnded(MatchSnapshot snap)
    {
        _matchStartUtc = default;  // reset for next match
        UpdatePresence(snap);
    }

    void OnDetached() => Push("game:closed", new RichPresence
    {
        Details = "Tracker idle",
        State = "Game not running",
        Assets = new Assets { LargeImageKey = "logo", LargeImageText = "BCA-Tracker" },
    });

    void UpdatePresence(MatchSnapshot snap)
    {
        if (snap.InMatch)
        {
            // Skip until the map name is resolved — otherwise we flap from
            // "in::?:9-9" to "in:Twilight Path:Backup 3v3:9-9" within a tick
            // and waste a presence update for the unresolved version.
            if (string.IsNullOrEmpty(snap.CurrentMap)
                || snap.CurrentMap.StartsWith("Unknown", StringComparison.Ordinal))
                return;

            // Lazy-init match start time. MatchStarted should have set this
            // already, but if the user launched the tracker mid-match the
            // event won't have fired and we'd push a default(DateTime)
            // timestamp which Discord can reject.
            if (_matchStartUtc == default)
                _matchStartUtc = DateTime.UtcNow.AddSeconds(-snap.MatchTime);

            string key = $"in:{snap.CurrentMap}:{snap.ModeName}:{snap.MyLives}-{snap.EnemyLives}";
            Push(key, new RichPresence
            {
                Details = $"In match · {SafeText(snap.CurrentMap, "?")}",
                State   = $"{SafeText(snap.ModeName, "?")} · {snap.MyLives} vs {snap.EnemyLives} lives",
                Assets  = new Assets
                {
                    LargeImageKey  = "ingame",
                    LargeImageText = SafeText(snap.CurrentMap, "Battle Core Arena"),
                    SmallImageKey  = "logo",
                    SmallImageText = "BCA-Tracker",
                },
                Timestamps = new Timestamps { Start = _matchStartUtc },
            });
        }
        else if (snap.IsLobby)
        {
            string map  = snap.Lobby?.MapName ?? "?";
            string mode = snap.Lobby?.ModeName ?? snap.ModeName ?? "?";
            string key  = $"lobby:{map}:{mode}";
            Push(key, new RichPresence
            {
                Details = $"Lobby · {map}",
                State   = mode,
                Assets  = new Assets { LargeImageKey = "logo", LargeImageText = "Battle Core Arena" },
            });
        }
        else if (snap.IsPostMatch)
        {
            var me = snap.Players.Find(p => p.IsLocal);
            string scoreline = me is not null
                ? $"K/D {me.Kills}/{me.Deaths}/{me.Assists}"
                : "Match ended";
            Push("postmatch", new RichPresence
            {
                Details = "Post-match",
                State = scoreline,
                Assets = new Assets { LargeImageKey = "logo", LargeImageText = "BCA-Tracker" },
            });
        }
        else if (snap.IsMainMenu)
        {
            string menu = BCAEnums.MainMenuStateName(snap.MainMenuState);
            Push($"menu:{menu}", new RichPresence
            {
                Details = "In menus",
                State = menu,
                Assets = new Assets { LargeImageKey = "logo", LargeImageText = "Battle Core Arena" },
            });
        }
        else
        {
            Push("waiting", new RichPresence
            {
                Details = "Battle Core Arena",
                State = SafeText(snap.StateName, "Loading"),
                Assets = new Assets { LargeImageKey = "logo", LargeImageText = "Battle Core Arena" },
            });
        }
    }

    void Push(string key, RichPresence presence)
    {
        if (_client is null) return;
        if (key == _lastKey) return;

        // Hard throttle. Discord rate-limits presence updates to ~5 per 20s.
        // We dedupe by key already, but in-match snapshots that change every
        // tick (lives, kills) can still produce a burst — throttle protects
        // us from going over the limit and getting silently dropped.
        DateTime now = DateTime.UtcNow;
        if (now - _lastPushUtc < MinPushInterval)
        {
            DiagLog.Write($"[Discord] Push key='{key}' THROTTLED (last push {(now - _lastPushUtc).TotalSeconds:0.0}s ago)");
            return;
        }

        try
        {
            _client.SetPresence(presence);
            _lastKey      = key;
            _lastPresence = presence;
            _lastPushUtc  = now;
            DiagLog.Write($"[Discord] Push key='{key}' connected={_connected}");
        }
        catch (Exception ex)
        {
            DiagLog.Exception("Discord SetPresence", ex);
        }
    }

    static string SafeText(string? s, string fallback)
        => string.IsNullOrWhiteSpace(s) ? fallback : s!;

    static string Mask(string s)
        => s.Length <= 6 ? "<short>" : $"{s[..4]}…{s[^4..]}";
}
