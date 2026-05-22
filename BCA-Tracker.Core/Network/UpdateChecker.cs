using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace BCATracker.Core
{
    /// <summary>
    /// Checks GitHub Releases for newer versions of the tracker.
    ///
    /// The flow:
    ///   1. On startup (and once per day after), GET the repo's latest
    ///      release from the GitHub API.
    ///   2. Parse the tag (e.g. "v0.16.0-beta1") into a comparable
    ///      version.
    ///   3. Compare with the running assembly's version.
    ///   4. If newer, expose <see cref="LatestVersion"/> and the URL
    ///      of the .exe asset so the UI can offer "Download update".
    ///
    /// The user clicks the banner, which downloads the new installer
    /// to a temp file and launches it. The new installer's Inno Setup
    /// machinery upgrades in place. Tracker exits.
    ///
    /// Failure modes are silent — if GitHub is unreachable, no update
    /// banner shows and the tracker works normally.
    /// </summary>
    public sealed class UpdateChecker
    {
        // The repo we check against. Owner/repo form.
        const string GitHubRepo = "SSI-BCA/BCA-Tracker";

        readonly HttpClient _http;

        public string CurrentVersion { get; }
        public string LatestVersion  { get; private set; } = "";
        public string LatestSetupUrl { get; private set; } = "";
        public bool   UpdateAvailable => !string.IsNullOrEmpty(LatestVersion)
                                         && CompareVersions(LatestVersion, CurrentVersion) > 0;

        public UpdateChecker()
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            // GitHub requires a User-Agent header on API requests.
            _http.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("BCA-Tracker", AssemblyVersionString()));
            _http.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            CurrentVersion = AssemblyVersionString();
        }

        /// <summary>
        /// Check once. Safe to call from a fire-and-forget Task.Run().
        /// Sets <see cref="LatestVersion"/> + <see cref="LatestSetupUrl"/>
        /// if a newer release exists. Catches all exceptions.
        /// </summary>
        public async Task CheckAsync()
        {
            try
            {
                string url = $"https://api.github.com/repos/{GitHubRepo}/releases/latest";
                using var resp = await _http.GetAsync(url).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    DiagLog.Write($"[Update] HTTP {(int)resp.StatusCode} from GitHub releases");
                    return;
                }
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var release = JsonSerializer.Deserialize<GhRelease>(body);
                if (release is null)
                {
                    DiagLog.Write("[Update] Couldn't parse release JSON.");
                    return;
                }

                string tag = (release.TagName ?? "").TrimStart('v');
                if (string.IsNullOrEmpty(tag)) return;

                // Find the .exe asset (the setup installer).
                string? setupUrl = null;
                if (release.Assets is not null)
                {
                    foreach (var a in release.Assets)
                    {
                        if (a.Name is null) continue;
                        if (a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                            && a.Name.Contains("Setup", StringComparison.OrdinalIgnoreCase))
                        {
                            setupUrl = a.BrowserDownloadUrl;
                            break;
                        }
                    }
                }
                if (string.IsNullOrEmpty(setupUrl))
                {
                    DiagLog.Write($"[Update] Release {tag} has no Setup .exe asset; skipping.");
                    return;
                }

                LatestVersion  = tag;
                LatestSetupUrl = setupUrl;
                DiagLog.Write($"[Update] Latest release on GitHub: {tag} (current={CurrentVersion})");
            }
            catch (Exception ex)
            {
                // Network down, GitHub blip, parse error — none of which
                // should bother the user. Just log and move on.
                DiagLog.Write($"[Update] Check failed: {ex.Message}");
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        static string AssemblyVersionString()
        {
            // Prefer InformationalVersion (which carries pre-release
            // tags) over AssemblyVersion (always 4-tuple, no suffix).
            var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrEmpty(info))
            {
                // Strip git-hash suffix added by .NET SDK (e.g. "0.15.0+abc123").
                int plus = info.IndexOf('+');
                if (plus > 0) info = info.Substring(0, plus);
                return info;
            }
            return asm.GetName().Version?.ToString() ?? "0.0.0";
        }

        /// <summary>
        /// Compare two version strings. Returns &gt;0 if a is newer, 0 if
        /// equal, &lt;0 if a is older. Handles plain SemVer ("1.2.3") and
        /// suffixed versions ("1.2.3-beta1") — suffixed versions sort
        /// BEFORE the corresponding plain version, matching SemVer rules.
        /// </summary>
        public static int CompareVersions(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return string.IsNullOrEmpty(b) ? 0 : -1;
            if (string.IsNullOrEmpty(b)) return 1;

            var (aCore, aSuffix) = SplitVersion(a);
            var (bCore, bSuffix) = SplitVersion(b);

            int coreCmp = CompareCore(aCore, bCore);
            if (coreCmp != 0) return coreCmp;

            // Same numeric core. SemVer: pre-release < release.
            //   "1.0.0-beta" < "1.0.0"
            if (aSuffix == "" && bSuffix == "") return 0;
            if (aSuffix == "") return 1;
            if (bSuffix == "") return -1;
            return string.Compare(aSuffix, bSuffix, StringComparison.OrdinalIgnoreCase);
        }

        static (int[] core, string suffix) SplitVersion(string v)
        {
            int dash = v.IndexOf('-');
            string corePart   = dash > 0 ? v.Substring(0, dash) : v;
            string suffixPart = dash > 0 ? v.Substring(dash + 1) : "";
            var parts = corePart.Split('.');
            var core = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                int.TryParse(parts[i], out core[i]);
            return (core, suffixPart);
        }

        static int CompareCore(int[] a, int[] b)
        {
            int len = Math.Max(a.Length, b.Length);
            for (int i = 0; i < len; i++)
            {
                int ai = i < a.Length ? a[i] : 0;
                int bi = i < b.Length ? b[i] : 0;
                if (ai != bi) return ai - bi;
            }
            return 0;
        }

        // ── GitHub Releases API payload (minimal shape) ─────────────────────
        sealed class GhRelease
        {
            [JsonPropertyName("tag_name")] public string? TagName { get; set; }
            [JsonPropertyName("name")]     public string? Name    { get; set; }
            [JsonPropertyName("assets")]   public GhAsset[]? Assets { get; set; }
        }
        sealed class GhAsset
        {
            [JsonPropertyName("name")]                 public string? Name              { get; set; }
            [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
        }
    }
}
