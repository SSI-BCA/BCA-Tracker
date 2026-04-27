using System;
using System.Diagnostics;
using System.Threading;

namespace BCATracker
{
    class Program
    {
        static void Main()
        {
            Console.Title    = "BCA Tracker — Alpha V0.13";
            Console.CursorVisible = false;

            var reader    = new MemoryReader();
            var killFeed  = new KillFeedTracker();
            var saver     = new MatchSaver();
            var timer     = new Stopwatch();
            byte lastState = 255;

            while (true)
            {
                try
                {
                    MainLoop(reader, killFeed, saver, timer, ref lastState);
                }
                catch (Exception ex)
                {
                    Console.Clear();
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  Error: {ex.Message}");
                    Console.WriteLine($"  {ex.StackTrace}");
                    Console.ResetColor();
                    Thread.Sleep(3000);
                }
            }
        }

        static void MainLoop(MemoryReader reader, KillFeedTracker killFeed,
                             MatchSaver saver, Stopwatch timer, ref byte lastState)
        {
            while (true)
            {
                var procs = Process.GetProcessesByName("BattleCoreArena");
                if (procs.Length == 0)
                {
                    ConsoleUI.Render(null);
                    Thread.Sleep(2000);
                    continue;
                }

                if (!reader.TryAttach(procs[0].Id))
                {
                    Console.Clear();
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("  OpenProcess failed. Run as Administrator.");
                    Console.ResetColor();
                    Thread.Sleep(3000);
                    continue;
                }

                // FNameResolver is per-session (pool base can shift between launches).
                // Constructed once per attach; a new one is created on re-attach.
                var nameResolver = new FNameResolver(reader);

                // NOTE: DllInjector / BCAFNameResolver.dll is no longer needed.
                // FNameResolver reads FNamePool directly via ReadProcessMemory.

                killFeed.Reset();

                while (reader.IsAttached)
                {
                    if (Process.GetProcessesByName("BattleCoreArena").Length == 0)
                    {
                        saver.Tick(null);
                        reader.Detach();
                        break;
                    }

                    var snap = reader.ReadSnapshot(killFeed, timer, ref lastState, nameResolver);
                    saver.Tick(snap);
                    ConsoleUI.Render(snap);
                    Thread.Sleep(500);
                }
            }
        }
    }
}
