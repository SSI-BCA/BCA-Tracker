using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace BCATracker.Core
{
    /// <summary>
    /// Background service that uploads completed match JSON files to an
    /// HTTP endpoint. Designed to be a fire-and-forget side effect of
    /// <see cref="MatchSaver"/> writing a new match file.
    ///
    /// Properties we care about:
    /// <list type="bullet">
    ///   <item>Opt-in. The service does nothing unless explicitly enabled
    ///   AND given an endpoint URL. Both defaults are off/empty.</item>
    ///   <item>Durable. Pending uploads are persisted to a queue file on
    ///   disk so an offline session, app crash, or PC shutdown doesn't
    ///   lose data — they retry on next launch.</item>
    ///   <item>Background. Network is on a worker thread, never on the
    ///   UI thread or the snapshot loop. Match recording is unaffected
    ///   if the backend is slow or down.</item>
    ///   <item>Bounded retry. Per-match attempt count caps at 10 with
    ///   exponential backoff. After that the file is moved to a
    ///   "failed" subfolder and the operator can inspect it manually.</item>
    /// </list>
    ///
    /// Usage:
    /// <code>
    ///   var uploader = new MatchUploadService();
    ///   uploader.Configure(enabled: true, endpoint: "https://...", accountId: "...");
    ///   uploader.Start();
    ///   ...
    ///   uploader.Enqueue(matchJsonPath);   // called by MatchSaver
    ///   ...
    ///   uploader.Stop();                    // on app shutdown
    /// </code>
    /// </summary>
    public class MatchUploadService : IDisposable
    {
        // ── Configuration (mutable at runtime) ──────────────────────────────

        readonly object _cfgLock = new object();
        bool   _enabled    = false;
        string _endpoint   = "";
        string _accountId  = "";

        // ── State ───────────────────────────────────────────────────────────

        readonly string _queueDir;
        readonly string _failedDir;

        readonly BlockingCollection<string> _pending =
            new BlockingCollection<string>(new ConcurrentQueue<string>());

        readonly HttpClient _http;
        readonly CancellationTokenSource _cts = new CancellationTokenSource();
        Thread? _worker;
        bool _started;
        bool _disposed;

        // ── Diagnostics counters (read-only externally) ─────────────────────

        int _uploadedCount;
        int _failedCount;
        int _pendingHint;
        DateTime _lastSuccessUtc = DateTime.MinValue;
        string _lastError = "";

        public int UploadedCount        => _uploadedCount;
        public int FailedCount          => _failedCount;
        public int PendingCount         => _pending.Count + _pendingHint;
        public DateTime LastSuccessUtc  => _lastSuccessUtc;
        public string LastError         => _lastError;

        // ── Construction ────────────────────────────────────────────────────

        public MatchUploadService()
        {
            string hub = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BCA-Tracker");
            _queueDir  = Path.Combine(hub, "upload-queue");
            _failedDir = Path.Combine(hub, "upload-failed");
            Directory.CreateDirectory(_queueDir);
            Directory.CreateDirectory(_failedDir);

            _http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30),
            };
            _http.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("BCA-Tracker", "1.0"));
        }

        public void Configure(bool enabled, string endpoint, string accountId)
        {
            string newEndpoint = (endpoint ?? "").TrimEnd('/');
            string newAccountId = accountId ?? "";
            bool changed;
            lock (_cfgLock)
            {
                changed = _enabled != enabled
                       || !string.Equals(_endpoint,  newEndpoint,  StringComparison.Ordinal)
                       || !string.Equals(_accountId, newAccountId, StringComparison.Ordinal);
                _enabled   = enabled;
                _endpoint  = newEndpoint;
                _accountId = newAccountId;
            }
            // Only log when something actually changed — otherwise the line
            // repeats every settings tick and floods the diag log.
            if (changed)
            {
                DiagLog.Write($"[Upload] Configure enabled={_enabled} " +
                              $"endpoint={(string.IsNullOrEmpty(_endpoint) ? "<none>" : _endpoint)} " +
                              $"accountId={(string.IsNullOrEmpty(_accountId) ? "<none>" : "set")}");
            }
        }

        public void Start()
        {
            if (_started) return;
            _started = true;

            // Re-queue anything left over from a previous session.
            try
            {
                foreach (string f in Directory.EnumerateFiles(_queueDir, "*.json"))
                    _pending.Add(f);
                int n = _pending.Count;
                if (n > 0) DiagLog.Write($"[Upload] Resuming {n} queued match(es) from previous session");
            }
            catch (Exception ex)
            {
                DiagLog.Write($"[Upload] Failed to scan queue dir: {ex.Message}");
            }

            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "BCA-MatchUploader",
            };
            _worker.Start();
        }

        public void Stop()
        {
            if (_disposed) return;
            _cts.Cancel();
            _pending.CompleteAdding();
            try { _worker?.Join(TimeSpan.FromSeconds(2)); } catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            _http.Dispose();
            _cts.Dispose();
            _pending.Dispose();
        }

        // ── Public API ──────────────────────────────────────────────────────

        /// <summary>
        /// Enqueue a freshly-written match JSON for upload. The file is
        /// COPIED into the upload-queue directory, so the original under
        /// matches/yyyy-MM/yyyy-MM-dd/ stays untouched and the queue copy
        /// is what gets retried on failure.
        ///
        /// Safe to call even when uploads are disabled — does nothing in
        /// that case (the file just stays on disk in the matches folder).
        /// </summary>
        public void Enqueue(string matchJsonPath)
        {
            bool enabled;
            string endpoint;
            lock (_cfgLock)
            {
                enabled  = _enabled;
                endpoint = _endpoint;
            }
            if (!enabled || string.IsNullOrEmpty(endpoint)) return;
            if (!File.Exists(matchJsonPath)) return;

            try
            {
                string queueName = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}_{Path.GetFileName(matchJsonPath)}";
                string queuePath = Path.Combine(_queueDir, queueName);
                File.Copy(matchJsonPath, queuePath, overwrite: true);
                _pending.Add(queuePath);
                DiagLog.Write($"[Upload] Queued {Path.GetFileName(matchJsonPath)} (queue depth ~{_pending.Count})");
            }
            catch (Exception ex)
            {
                DiagLog.Write($"[Upload] Failed to enqueue {matchJsonPath}: {ex.Message}");
                _lastError = ex.Message;
            }
        }

        // ── Worker loop ─────────────────────────────────────────────────────

        void WorkerLoop()
        {
            try
            {
                foreach (string file in _pending.GetConsumingEnumerable(_cts.Token))
                {
                    if (_cts.IsCancellationRequested) break;

                    bool   enabled;
                    string endpoint;
                    string accountId;
                    lock (_cfgLock)
                    {
                        enabled   = _enabled;
                        endpoint  = _endpoint;
                        accountId = _accountId;
                    }

                    if (!enabled || string.IsNullOrEmpty(endpoint))
                    {
                        // Disabled mid-flight. Leave the file in the queue
                        // so it picks back up if/when the user re-enables.
                        // We DON'T break out of the loop — we keep draining
                        // any remaining items but skip uploading them. The
                        // next Start() call will re-scan the queue dir.
                        continue;
                    }

                    UploadOne(file, endpoint, accountId).GetAwaiter().GetResult();
                }
            }
            catch (OperationCanceledException) { /* shutting down */ }
            catch (Exception ex)
            {
                DiagLog.Write($"[Upload] Worker crashed: {ex}");
            }
        }

        async Task UploadOne(string queuePath, string endpoint, string accountId)
        {
            const int MaxAttempts = 10;
            int attempt = 0;
            Exception? lastEx = null;

            while (attempt < MaxAttempts && !_cts.IsCancellationRequested)
            {
                attempt++;
                try
                {
                    if (!File.Exists(queuePath))
                    {
                        // Removed between scan and upload. Nothing to do.
                        return;
                    }

                    string json = File.ReadAllText(queuePath);
                    string url  = endpoint + "/v1/matches";

                    using var content = new StringContent(json, Encoding.UTF8, "application/json");
                    using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                    if (!string.IsNullOrEmpty(accountId))
                        req.Headers.TryAddWithoutValidation("X-BCA-Account-Id", accountId);
                    req.Headers.TryAddWithoutValidation("X-BCA-Source-File",
                        Path.GetFileName(queuePath));

                    using var resp = await _http.SendAsync(req, _cts.Token).ConfigureAwait(false);

                    if ((int)resp.StatusCode >= 200 && (int)resp.StatusCode < 300)
                    {
                        // Success — drop the queued copy.
                        try { File.Delete(queuePath); } catch { }
                        Interlocked.Increment(ref _uploadedCount);
                        _lastSuccessUtc = DateTime.UtcNow;
                        _lastError = "";
                        DiagLog.Write($"[Upload] OK {Path.GetFileName(queuePath)} " +
                                      $"({(int)resp.StatusCode} after {attempt} attempt(s))");
                        return;
                    }

                    // 4xx (other than 408/429) means the server doesn't want
                    // this file ever — don't keep retrying forever.
                    int status = (int)resp.StatusCode;
                    if (status >= 400 && status < 500 && status != 408 && status != 429)
                    {
                        await MoveToFailed(queuePath, $"http_{status}").ConfigureAwait(false);
                        Interlocked.Increment(ref _failedCount);
                        _lastError = $"server rejected ({status})";
                        DiagLog.Write($"[Upload] Rejected {Path.GetFileName(queuePath)} ({status}) - moved to failed/");
                        return;
                    }

                    lastEx = new HttpRequestException($"HTTP {status}");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    lastEx = ex;
                }

                // Exponential backoff with cap. 1s, 2s, 4s, 8s, 16s, 30s, 30s...
                int waitMs = (int)Math.Min(30_000, 1000 * Math.Pow(2, attempt - 1));
                _lastError = lastEx?.Message ?? "unknown";
                DiagLog.Write($"[Upload] Attempt {attempt}/{MaxAttempts} failed for " +
                              $"{Path.GetFileName(queuePath)}: {_lastError} — retry in {waitMs}ms");
                try { await Task.Delay(waitMs, _cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
            }

            // Exhausted attempts.
            await MoveToFailed(queuePath, "max_attempts").ConfigureAwait(false);
            Interlocked.Increment(ref _failedCount);
            DiagLog.Write($"[Upload] Giving up on {Path.GetFileName(queuePath)} after {MaxAttempts} attempts " +
                          $"(last error: {_lastError}) — moved to failed/");
        }

        async Task MoveToFailed(string queuePath, string reasonTag)
        {
            await Task.Yield();   // keep the signature async-friendly
            try
            {
                if (!File.Exists(queuePath)) return;
                string baseName = Path.GetFileName(queuePath);
                string dest = Path.Combine(_failedDir, $"{reasonTag}_{baseName}");
                // Avoid clobbering a previous failure with the same name.
                int n = 1;
                while (File.Exists(dest))
                {
                    dest = Path.Combine(_failedDir, $"{reasonTag}_{n}_{baseName}");
                    n++;
                }
                File.Move(queuePath, dest);
            }
            catch (Exception ex)
            {
                DiagLog.Write($"[Upload] Failed to move {queuePath} to failed/: {ex.Message}");
            }
        }
    }
}
