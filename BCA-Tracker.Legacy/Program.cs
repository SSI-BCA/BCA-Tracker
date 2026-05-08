using System;
using System.Diagnostics;
using System.Threading;
using BCATracker.Core;

namespace BCATracker.Legacy
{
    class Program
    {
        static void Main()
        {
            Console.Title = "BCA Tracker — Alpha V0.13";
            Console.CursorVisible = false;

            DiagLog.Init();
            DiagLog.Write("[Program] Startup");

            var reader = new MemoryReader();
            var killFeed = new KillFeedTracker();
            var saver = new MatchSaver();
            var timer = new Stopwatch();
            byte lastState = 255;

            while (true)
            {
                try { MainLoop(reader, killFeed, saver, timer, ref lastState); }
                catch (Exception ex)
                {
                    DiagLog.Exception("MainLoop", ex);
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

                DiagLog.ProcessFound(procs[0].Id);

                if (!reader.TryAttach(procs[0].Id))
                {
                    DiagLog.ProcessAttachFailed();
                    Console.Clear();
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("  OpenProcess failed. Run as Administrator.");
                    Console.ResetColor();
                    Thread.Sleep(3000);
                    continue;
                }

                DiagLog.ProcessAttached(reader.ModuleBase);

                var nameResolver = new FNameResolver(reader);
                killFeed.Reset();

                while (reader.IsAttached)
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
                        ConsoleUI.Render(snap);
                    }
                    catch (Exception ex)
                    {
                        DiagLog.Exception("ReadSnapshot/Tick", ex);
                    }

                    Thread.Sleep(500);
                }
            }
        }
    }
}