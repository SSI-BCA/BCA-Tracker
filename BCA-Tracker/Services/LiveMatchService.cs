using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using BCATracker.Core;

namespace BCATracker.UI.Services;

/// <summary>
/// Wraps the same memory-reader loop the Legacy console runs, but as a
/// background <see cref="Task"/> that raises events into the UI dispatcher.
///
/// Lifecycle:
///   • Construct once at app startup.
///   • Start() spins up the background task.
///   • Subscribe to events.
///   • StopAsync() at shutdown.
///
/// Threading contract:
///   • Events ALWAYS fire on the UI thread (we marshal via Dispatcher.UIThread).
///     Subscribers can safely touch Avalonia controls from handlers.
///   • The Snapshot property is updated atomically from the reader thread.
///     UI code reading it gets a consistent reference but the contents may
///     mutate as the next read finishes.
/// </summary>
public sealed class LiveMatchService : IDisposable
{
    readonly CancellationTokenSource _cts = new();
    Task? _loopTask;

    /// <summary>Fires whenever the reader produces a new snapshot. UI thread.</summary>
    public event Action<MatchSnapshot>? Tick;

    /// <summary>Fires when match enters playable state (lobby → in-match transition). UI thread.</summary>
    public event Action<MatchSnapshot>? MatchStarted;

    /// <summary>Fires when the player leaves an in-match state. UI thread.</summary>
    public event Action<MatchSnapshot>? MatchEnded;

    /// <summary>Fires when reader attaches to the game process. UI thread.</summary>
    public event Action? Attached;

    /// <summary>Fires when reader detaches (process closed, OpenProcess failed). UI thread.</summary>
    public event Action? Detached;

    /// <summary>Latest snapshot, or null if reader isn't currently reading.</summary>
    public MatchSnapshot? Snapshot { get; private set; }

    /// <summary>True when the reader is actively reading from the game process.</summary>
    public bool IsAttached { get; private set; }

    public void Start()
    {
        if (_loopTask is not null) return;
        _loopTask = Task.Run(() => RunLoop(_cts.Token));
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        if (_loopTask is not null)
        {
            try { await _loopTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    void RunLoop(CancellationToken ct)
    {
        var reader   = new MemoryReader();
        var killFeed = new KillFeedTracker();
        var saver    = new MatchSaver();
        var timer    = new Stopwatch();
        byte lastState = 255;
        bool wasInMatch = false;

        // Outer loop: scan for the game process, attach when found.
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var procs = Process.GetProcessesByName("BattleCoreArena");
                if (procs.Length == 0)
                {
                    if (IsAttached) { IsAttached = false; PostUI(() => Detached?.Invoke()); }
                    Snapshot = null;
                    Sleep(2000, ct);
                    continue;
                }

                if (!reader.TryAttach(procs[0].Id))
                {
                    DiagLog.ProcessAttachFailed();
                    Sleep(3000, ct);
                    continue;
                }

                DiagLog.ProcessAttached(reader.ModuleBase);
                IsAttached = true;
                PostUI(() => Attached?.Invoke());

                var nameResolver = new FNameResolver(reader);
                killFeed.Reset();

                // Inner loop: read snapshots while still attached.
                while (reader.IsAttached && !ct.IsCancellationRequested)
                {
                    if (Process.GetProcessesByName("BattleCoreArena").Length == 0)
                    {
                        DiagLog.ProcessLost();
                        saver.Tick(null);
                        reader.Detach();
                        break;
                    }

                    try
                    {
                        var snap = reader.ReadSnapshot(killFeed, timer, ref lastState, nameResolver);
                        DiagLog.SnapState(snap);
                        saver.Tick(snap);
                        Snapshot = snap;

                        bool nowInMatch = snap.InMatch;
                        bool justStarted = !wasInMatch && nowInMatch;
                        bool justEnded   = wasInMatch && !nowInMatch;

                        // Clear the *visible* kill feed when a fresh match
                        // begins so the new round starts empty. We do NOT call
                        // killFeed.Reset() here — that would also clear the
                        // per-player baselines (_prevHitCount etc.), which
                        // alpha kept persistent across matches. Reset is only
                        // for fresh attach.
                        if (justStarted) killFeed.ClearVisibleFeed();

                        // Update wasInMatch BEFORE the PostUI lambda runs,
                        // since the next loop iteration could overwrite it
                        // before the dispatcher gets to our handler. We read
                        // the snapshot flags into locals (justStarted,
                        // justEnded) which the lambda captures by value.
                        wasInMatch = nowInMatch;

                        if (justStarted) DiagLog.Write("[Live] MatchStarted firing — was lobby/menu, now in-match");
                        if (justEnded)   DiagLog.Write("[Live] MatchEnded firing — was in-match, now post-match/lobby");

                        PostUI(() =>
                        {
                            Tick?.Invoke(snap);
                            if (justStarted) MatchStarted?.Invoke(snap);
                            if (justEnded)   MatchEnded?.Invoke(snap);
                        });
                    }
                    catch (Exception ex)
                    {
                        DiagLog.Exception("LiveMatchService.ReadSnapshot", ex);
                    }

                    Sleep(500, ct);
                }

                IsAttached = false;
                PostUI(() => Detached?.Invoke());
            }
            catch (Exception ex)
            {
                DiagLog.Exception("LiveMatchService.OuterLoop", ex);
                Sleep(3000, ct);
            }
        }
    }

    static void Sleep(int ms, CancellationToken ct)
    {
        try { Task.Delay(ms, ct).Wait(ct); }
        catch (OperationCanceledException) { }
        catch (AggregateException) { }
    }

    static void PostUI(Action a)
    {
        // Marshal to UI thread; if Avalonia isn't running yet (very early
        // startup) we just drop the call. UI hasn't subscribed anyway.
        try { Dispatcher.UIThread.Post(a); }
        catch { /* best effort */ }
    }
}
