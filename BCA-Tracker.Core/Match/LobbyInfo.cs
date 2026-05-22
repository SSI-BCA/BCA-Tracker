using System;

namespace BCATracker.Core
{
    /// <summary>
    /// Read from a CustomGameGS in memory each polling tick when the local
    /// player is in a custom lobby. The publisher service ships these to
    /// the backend (when hosting and advertising is enabled); the browser
    /// receives them.
    ///
    /// All fields come straight from the game's state, with one exception:
    /// <see cref="HostExternalIP"/> and <see cref="HostExternalPort"/> are
    /// supplied by the tracker's own UPnP service — the game doesn't know
    /// its external NAT mapping.
    /// </summary>
    public sealed class LobbyInfo
    {
        // ── Identity ──────────────────────────────────────────────────────────

        /// <summary>The host's stable per-player GUID, read from
        /// CustomGamePS.CustomProfileID. Identifies the same player across
        /// sessions without us inventing our own ID.</summary>
        public string HostProfileId { get; set; } = "";

        /// <summary>The host's in-game display name (from the engine
        /// PlayerState). Joiners see this in the browser.</summary>
        public string HostName { get; set; } = "";

        // ── Lobby properties (from CustomGameGS) ──────────────────────────────

        /// <summary>FText at GS+0x310. The lobby's display name — what the
        /// host typed into "Game name" in the create-lobby screen.</summary>
        public string LobbyName { get; set; } = "";

        /// <summary>FName at GS+0x328. The map row name (raw, before
        /// MapRowNameToDisplayName translates it).</summary>
        public string MapRowName { get; set; } = "";

        /// <summary>FName at GS+0x338. The game-mode row name.</summary>
        public string GameModeRowName { get; set; } = "";

        /// <summary>int32 at GS+0x418. Max players per team. Total lobby
        /// capacity is roughly 2× this for 2-team modes.</summary>
        public int MaxTeamSize { get; set; }

        /// <summary>Length of ConnectedPlayerStates array at GS+0x3F0.
        /// Number of players currently in the lobby including the host
        /// and any bots that've been added.</summary>
        public int CurrentPlayerCount { get; set; }

        /// <summary>True if LobbyPassword (FGuid at GS+0x3D0) is non-zero.
        /// We never send the password itself to the backend — only the
        /// flag. The host has to share the password out-of-band; the
        /// password gets entered in-game, not in the tracker.</summary>
        public bool HasPassword { get; set; }

        // ── Network info (from UPnP service, not memory) ──────────────────────

        /// <summary>External IP the host advertised, as learned from UPnP
        /// or an external IP lookup. Joiners connect here.</summary>
        public string HostExternalIP { get; set; } = "";

        /// <summary>External port the host's router is forwarding to the
        /// game's listen port. Joiners connect to IP:Port.</summary>
        public int HostExternalPort { get; set; }

        /// <summary>
        /// NetBird group ID for this lobby. The host's tracker calls
        /// the backend to provision the group + setup key + ACL policy;
        /// the group ID is the public identifier joiners use to fetch
        /// the setup key (via /v1/nb/lobbies/{groupId}/join) and enroll
        /// their local NetBird agent. Empty for lobbies that don't use
        /// NetBird.
        /// </summary>
        public string NetBirdGroupId { get; set; } = "";

        // ── Liveness ──────────────────────────────────────────────────────────

        /// <summary>UTC timestamp this snapshot was captured. The backend
        /// uses this to expire lobbies that haven't been heartbeated in a
        /// while (e.g. >2 minutes → consider the lobby dead).</summary>
        public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// True if the local player IS the host (CustomGamePS.IsGameLeader
        /// at offset 0x3F4). Only the host should advertise; non-host
        /// players in the same lobby see this as false and the publisher
        /// stays quiet.
        /// </summary>
        public bool LocalPlayerIsHost { get; set; }
    }
}
