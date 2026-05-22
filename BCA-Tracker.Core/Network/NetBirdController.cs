using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BCATracker.Core
{
    /// <summary>
    /// Drives the local NetBird service on Windows. The tracker uses
    /// NetBird as a transparent virtual WireGuard LAN: when hosting a
    /// lobby, the backend creates a group + setup key on our NetBird
    /// management server, hands the key to the tracker, and the
    /// tracker calls "netbird up --setup-key …" to enroll the local
    /// machine into the lobby's group.
    ///
    /// Users never see this — there's no signup, no token, no NetBird
    /// UI interaction beyond approving the one-time installer.
    ///
    /// The CLI normally lives at C:\Program Files\NetBird\netbird.exe
    /// after a standard MSI install, but we also probe a few alternate
    /// locations and PATH in case the user installed elsewhere.
    /// </summary>
    public sealed class NetBirdController
    {
        // All places we know NetBird might land. Order matters — first
        // existing path wins.
        static readonly string[] _installDirCandidates =
        {
            @"C:\Program Files\NetBird",
            @"C:\Program Files (x86)\NetBird",
            // Per-user install location used by some MSI configurations:
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "NetBird"),
        };

        // Resolved on first access and cached. Empty if not found.
        string? _cliPathCache;

        /// <summary>True if a netbird.exe was found in any expected
        /// location or on PATH.</summary>
        public bool IsInstalled => !string.IsNullOrEmpty(CliPath);

        /// <summary>Full path to netbird.exe, or empty if not found.</summary>
        public string CliPath
        {
            get
            {
                if (_cliPathCache is not null) return _cliPathCache;
                _cliPathCache = FindCli();
                if (!string.IsNullOrEmpty(_cliPathCache))
                    DiagLog.Write($"[NB] Resolved CLI at {_cliPathCache}");
                return _cliPathCache;
            }
        }

        static string FindCli()
        {
            foreach (var dir in _installDirCandidates)
            {
                try
                {
                    string p = Path.Combine(dir, "netbird.exe");
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
            // Fall back to PATH lookup.
            try
            {
                var path = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var dir in path.Split(';'))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    try
                    {
                        string p = Path.Combine(dir, "netbird.exe");
                        if (File.Exists(p)) return p;
                    }
                    catch { }
                }
            }
            catch { }
            return "";
        }

        /// <summary>
        /// Force a re-scan of install paths. Call after running the
        /// installer so a freshly-installed netbird.exe is picked up
        /// without restarting the tracker.
        /// </summary>
        public void Refresh() => _cliPathCache = null;

        /// <summary>
        /// Install NetBird silently using the bundled MSI. The MSI sits
        /// next to the tracker executable as "netbird_installer.msi"
        /// (or one of the alternate names we check). UAC prompts the
        /// user once.
        /// </summary>
        public async Task<bool> InstallAsync(string msiPath, CancellationToken ct = default)
        {
            if (!File.Exists(msiPath))
            {
                DiagLog.Write($"[NB] Installer not found at {msiPath}");
                return false;
            }
            // Sanity check: msiexec accepts only OLE compound documents
            // (magic D0 CF 11 E0). If we hand it an EXE or HTML it errors
            // out with the unhelpful "package could not be opened" dialog.
            // Detect that here and log a useful message instead.
            try
            {
                var head = new byte[8];
                using (var fs = File.OpenRead(msiPath))
                {
                    int n = fs.Read(head, 0, head.Length);
                    if (n < 8 ||
                        head[0] != 0xD0 || head[1] != 0xCF ||
                        head[2] != 0x11 || head[3] != 0xE0)
                    {
                        string hex = BitConverter.ToString(head, 0, n);
                        long sz = new FileInfo(msiPath).Length;
                        DiagLog.Write(
                            $"[NB] {msiPath} isn't a valid MSI " +
                            $"(size={sz}B, first bytes={hex}). The build " +
                            $"probably packaged the NetBird .exe by mistake. " +
                            $"Download the real MSI from " +
                            $"https://github.com/netbirdio/netbird/releases " +
                            $"and run it manually.");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                DiagLog.Write($"[NB] MSI sanity check failed: {ex.GetType().Name}: {ex.Message}");
                // Continue anyway — msiexec might still succeed.
            }
            try
            {
                var psi = new ProcessStartInfo("msiexec.exe",
                    $"/i \"{msiPath}\" /qn /norestart")
                {
                    UseShellExecute = true,    // required for UAC elevation
                    Verb = "runas",
                    CreateNoWindow = true,
                };
                var p = Process.Start(psi);
                if (p is null) return false;
                await p.WaitForExitAsync(ct);
                int code = p.ExitCode;
                // Translate common msiexec exit codes for the log so we
                // know what failed without spelunking error code tables.
                string msg = code switch
                {
                    0    => "success",
                    1602 => "user cancelled",
                    1603 => "fatal error during installation",
                    1618 => "another install is already in progress",
                    1620 => "installer package invalid (or downgrade attempt)",
                    1638 => "already installed (different version)",
                    3010 => "success, reboot required",
                    _    => "unknown",
                };
                DiagLog.Write($"[NB] msiexec exit code {code} ({msg})");
                // 3010 = success with reboot pending; we treat that as OK.
                // 1638 = "already installed, different version" — also OK
                // for our purposes, NetBird is there, just maybe a
                // different version.
                bool ok = code == 0 || code == 3010 || code == 1638;
                if (ok) Refresh();
                return ok;
            }
            catch (Exception ex)
            {
                DiagLog.Write($"[NB] Install failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Enroll the local agent onto a lobby's network using a setup
        /// key. <paramref name="managementUrl"/> is the public URL of
        /// the operator's NetBird management server (returned by the
        /// backend together with the setup key).
        ///
        /// We always run `netbird down` first. The CLI's `up` is not
        /// idempotent across management URLs: if the daemon is already
        /// connected to some other network, `up --setup-key <new>`
        /// often exits 0 in tens of ms without actually re-enrolling
        /// (it treats "already up" as a no-op). That's the bug that bit
        /// us with the manual NetBird installs: ldenn's agent was
        /// "already up" against the public netbird.io and silently
        /// ignored the lobby's setup key. Forcing `down` first
        /// guarantees a real handshake.
        /// </summary>
        public async Task<bool> UpAsync(string managementUrl, string setupKey, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(managementUrl) || string.IsNullOrEmpty(setupKey))
                return false;

            // Best-effort disconnect first. We don't care if it fails
            // (it'll fail if the daemon wasn't connected, which is the
            // happy case anyway). Just makes the subsequent `up` a
            // real enrollment.
            try
            {
                var (downOk, _) = await RunCliAsync(new[] { "down" }, ct);
                DiagLog.Write($"[NB] pre-up `down` ok={downOk}");
            }
            catch (Exception ex)
            {
                DiagLog.Write($"[NB] pre-up `down` threw: {ex.GetType().Name}: {ex.Message}");
            }

            var (ok, _) = await RunCliAsync(
                new[] {
                    "up",
                    "--management-url", managementUrl,
                    "--setup-key", setupKey,
                },
                ct);
            return ok;
        }

        /// <summary>Disconnect from the network. Idempotent.</summary>
        public async Task<bool> DownAsync(CancellationToken ct = default)
        {
            var (ok, _) = await RunCliAsync(new[] { "down" }, ct);
            return ok;
        }

        // Set to true once we've logged the raw status JSON for shape verification.
        bool _statusShapeLogged;

        /// <summary>
        /// Read the local NetBird agent's status. Returns the assigned
        /// IP and the management URL it's connected to; empty IP means
        /// not yet up.
        /// </summary>
        public async Task<NetBirdStatus> GetStatusAsync(CancellationToken ct = default)
        {
            var (ok, output) = await RunCliAsync(new[] { "status", "-j" }, ct);
            if (!ok) return new NetBirdStatus();
            try
            {
                using var doc = JsonDocument.Parse(output);
                var root = doc.RootElement;
                var st = new NetBirdStatus();
                // netbird status -j returns an object whose shape has
                // varied across versions; we look up fields defensively.
                if (root.TryGetProperty("netbirdIp", out var ipProp))
                    st.IP = StripCidr(ipProp.GetString() ?? "");
                else if (root.TryGetProperty("ip", out var ipProp2))
                    st.IP = StripCidr(ipProp2.GetString() ?? "");

                if (root.TryGetProperty("managementState", out var mgmtState))
                    st.ManagementConnected = string.Equals(
                        mgmtState.GetString(), "Connected",
                        StringComparison.OrdinalIgnoreCase);
                if (root.TryGetProperty("daemonStatus", out var d))
                    st.DaemonStatus = d.GetString() ?? "";

                // First time around, dump the raw JSON (truncated) and a
                // summary of the keys present, so we can debug shape
                // mismatches against the NetBird version installed.
                if (!_statusShapeLogged)
                {
                    _statusShapeLogged = true;
                    string raw = output.Trim();
                    if (raw.Length > 1024) raw = raw.Substring(0, 1024) + "…[truncated]";
                    var keys = new System.Text.StringBuilder();
                    foreach (var prop in root.EnumerateObject())
                    {
                        if (keys.Length > 0) keys.Append(',');
                        keys.Append(prop.Name);
                    }
                    DiagLog.Write($"[NB] status JSON keys: {keys}");
                    DiagLog.Write($"[NB] status raw: {raw}");
                }
                DiagLog.Write($"[NB] status parsed: ip={(string.IsNullOrEmpty(st.IP) ? "-" : st.IP)} mgmtConnected={st.ManagementConnected} daemon={st.DaemonStatus}");
                return st;
            }
            catch (Exception ex)
            {
                DiagLog.Write($"[NB] status parse: {ex.GetType().Name}: {ex.Message}");
                return new NetBirdStatus();
            }
        }

        /// <summary>
        /// Poll for an assigned IP after enrolling. NetBird usually
        /// completes the handshake within a few seconds, but on a cold
        /// start the daemon may need longer.
        /// </summary>
        public async Task<string> WaitForAssignedIPAsync(int timeoutSeconds = 30, CancellationToken ct = default)
        {
            var start = DateTime.UtcNow;
            var deadline = start.AddSeconds(timeoutSeconds);
            int polls = 0;
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                polls++;
                var s = await GetStatusAsync(ct);
                if (!string.IsNullOrEmpty(s.IP))
                {
                    DiagLog.Write($"[NB] WaitForIP: got {s.IP} after {polls} poll(s) in {(DateTime.UtcNow - start).TotalMilliseconds:F0}ms");
                    return s.IP;
                }
                await Task.Delay(1000, ct);
            }
            DiagLog.Write($"[NB] WaitForIP: timed out after {polls} poll(s) ({(DateTime.UtcNow - start).TotalSeconds:F1}s)");
            return "";
        }

        // ── Internals ───────────────────────────────────────────────────────

        async Task<(bool ok, string output)> RunCliAsync(string[] args, CancellationToken ct)
        {
            if (!IsInstalled)
            {
                DiagLog.Write("[NB] CLI not installed");
                return (false, "");
            }
            // Mask the setup-key value when logging — it's a secret.
            string argsForLog = MaskSetupKey(args);
            var started = DateTime.UtcNow;
            try
            {
                var psi = new ProcessStartInfo(CliPath)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                };
                foreach (var a in args) psi.ArgumentList.Add(a);

                DiagLog.Write($"[NB] exec: {CliPath} {argsForLog}");

                using var p = Process.Start(psi);
                if (p is null)
                {
                    DiagLog.Write("[NB] Process.Start returned null");
                    return (false, "");
                }

                string stdout = await p.StandardOutput.ReadToEndAsync(ct);
                string stderr = await p.StandardError.ReadToEndAsync(ct);
                await p.WaitForExitAsync(ct);
                double ms = (DateTime.UtcNow - started).TotalMilliseconds;

                bool ok = p.ExitCode == 0;
                DiagLog.Write($"[NB] exit={p.ExitCode} ({ms:F0}ms) stdoutLen={stdout.Length} stderrLen={stderr.Length}");
                // Always log stderr if anything came out — NetBird often writes
                // informational text there even on success.
                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    string trimmed = stderr.Trim();
                    if (trimmed.Length > 1024) trimmed = trimmed.Substring(0, 1024) + "…[truncated]";
                    DiagLog.Write("[NB] stderr: " + trimmed);
                }
                if (!ok && !string.IsNullOrWhiteSpace(stdout))
                {
                    string trimmed = stdout.Trim();
                    if (trimmed.Length > 1024) trimmed = trimmed.Substring(0, 1024) + "…[truncated]";
                    DiagLog.Write("[NB] stdout (on failure): " + trimmed);
                }
                else if (ok && !string.IsNullOrWhiteSpace(stdout) && stdout.Trim().Length <= 256)
                {
                    // On success, a tiny stdout often carries useful info
                    // like "Connected" or "already up" that helps distinguish
                    // a real enrollment from a silent no-op. Skip if it's
                    // long — that's likely a status dump we don't want
                    // to mirror twice.
                    DiagLog.Write("[NB] stdout: " + stdout.Trim());
                }
                return (ok, stdout);
            }
            catch (Exception ex)
            {
                DiagLog.Write($"[NB] CLI run failed: {ex.GetType().Name}: {ex.Message}");
                return (false, "");
            }
        }

        static string MaskSetupKey(string[] args)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < args.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                if (i > 0 && string.Equals(args[i - 1], "--setup-key", StringComparison.Ordinal))
                {
                    int len = args[i]?.Length ?? 0;
                    sb.Append($"<key:{len}b>");
                }
                else sb.Append(args[i]);
            }
            return sb.ToString();
        }

        static string StripCidr(string s)
        {
            int slash = s.IndexOf('/');
            return slash > 0 ? s.Substring(0, slash) : s;
        }
    }

    public sealed class NetBirdStatus
    {
        public string IP { get; set; } = "";
        public bool   ManagementConnected { get; set; }
        public string DaemonStatus { get; set; } = "";
    }
}
