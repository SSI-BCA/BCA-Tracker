using System;
using System.Collections.Generic;

namespace BCATracker
{
    public static class ConsoleUI
    {
        const string VERSION = "ALPHA V0.13";

        public static void Render(MatchSnapshot snap)
        {
            Console.Clear();
            DrawHeader();

            if (snap == null || snap.IsWaiting) { DrawWaiting(snap); return; }
            if (snap.IsMainMenu)  { DrawMainMenu(snap);  return; }
            if (snap.IsLobby)     { DrawLobby(snap);     return; }
            if (snap.IsPostMatch) { DrawPostMatch(snap);  return; }
            DrawInMatch(snap);
        }

        // ── Header ────────────────────────────────────────────────────────────────
        static void DrawHeader()
        {
            C(ConsoleColor.DarkMagenta);
            L("  ╔════════════════════════════════════════════════════════════════════════════════╗");
            L($"  ║                      BCA TRACKER — {VERSION,-43} ║");
            L("  ╚════════════════════════════════════════════════════════════════════════════════╝");
            R();
            Console.WriteLine();
        }

        // ── Waiting ───────────────────────────────────────────────────────────────
        static void DrawWaiting(MatchSnapshot snap)
        {
            C(ConsoleColor.DarkGray);
            L("  Waiting for match...");
            L($"  {Timestamp(snap)}");
            R();
        }

        // ── Main menu ─────────────────────────────────────────────────────────────
        static void DrawMainMenu(MatchSnapshot snap)
        {
            C(ConsoleColor.White);   L("  MAIN MENU"); R();
            C(ConsoleColor.DarkGray); L("  " + Dashes(40)); R();
            Console.Write("  Screen: ");
            C(ConsoleColor.Yellow); L(BCAEnums.MainMenuStateName(snap.MainMenuState)); R();
            Console.WriteLine();
            C(ConsoleColor.DarkGray); L($"  {Timestamp(snap)}"); R();
        }

        // ── Lobby ─────────────────────────────────────────────────────────────────
        static void DrawLobby(MatchSnapshot snap)
        {
            var lobby = snap.Lobby;
            C(ConsoleColor.White);   L("  CUSTOM GAME LOBBY"); R();
            C(ConsoleColor.DarkGray); L("  " + Dashes(40)); R();
            Console.Write("  Map:  "); C(ConsoleColor.Cyan);   L(lobby.MapName);  R();
            Console.Write("  Mode: "); C(ConsoleColor.Yellow); L(lobby.ModeName); R();
            Console.Write("  Bots: "); C(ConsoleColor.DarkGray);
            L($"Team 1: {lobby.BotCountT1}   Team 2: {lobby.BotCountT2}"); R();
            Console.WriteLine();
            C(ConsoleColor.DarkGray); L($"  {Timestamp(snap)}"); R();
        }

        // ── Post-match summary ────────────────────────────────────────────────────
        static void DrawPostMatch(MatchSnapshot snap)
        {
            bool isFFA    = snap.GameModeEnum == 4; // BackupFFA
            bool isQBall  = snap.GameModeEnum == 5; // GoldenCore3v3

            C(ConsoleColor.White);
            L($"  MATCH SUMMARY — {snap.ModeName}   [{snap.StateName}]");
            R();
            C(ConsoleColor.DarkGray); L("  " + Dashes(125)); R();

            // Header row
            C(ConsoleColor.DarkGray);
            string header = "  " + Col("Name", 20) + Col("Team", 6) + Col("K/D/A", 12) +
                            Col("Acc", 7) + Col("AbilAcc", 9) + Col("DMG", 8) + Col("HEAL", 8) +
                            Col("Hits±", 9) + Col("Dashes", 8) + Col("AbilUse", 9) +
                            Col("ShldPick", 10) + Col("Alive", 10);
            if (isFFA)   header += Col("BackUp", 8) + Col("Rank", 6);
            if (isQBall) header += Col("Score", 7);
            header += "Result";
            L(header);
            L("  " + Dashes(125));
            R();

            var sorted = new List<PlayerInfo>(snap.Players);
            if (isFFA)
                sorted.Sort((a, b) => a.FfaDeathRanking > 0 && b.FfaDeathRanking > 0
                    ? a.FfaDeathRanking.CompareTo(b.FfaDeathRanking)
                    : b.Kills != a.Kills ? b.Kills.CompareTo(a.Kills) : a.Deaths.CompareTo(b.Deaths));
            else
                sorted.Sort((a, b) => b.Kills != a.Kills ? b.Kills.CompareTo(a.Kills) : a.Deaths.CompareTo(b.Deaths));

            foreach (var p in sorted)
            {
                string kda      = $"{p.Kills}/{p.Deaths}/{p.Assists}";
                string acc      = $"{p.Accuracy:F0}%";
                string abilAcc  = p.AbilitiesUsed > 0 ? $"{p.AbilityAccuracy:F0}%" : "-";
                string dmg      = p.Damage > 0  ? p.Damage.ToString("F0") : "-";
                string heal     = p.Heal > 0    ? p.Heal.ToString("F0")   : "-";
                string hits     = $"+{p.NbHitsCaused}/-{p.NbHitsReceived}";
                string alive    = p.FormatAlive();
                string result   = p.IsWinner ? "WIN" : "LOSS";

                C(p.IsBot ? ConsoleColor.DarkGray : p.IsWinner ? ConsoleColor.Green : ConsoleColor.White);
                Console.Write("  " + Col(p.DisplayName, 20)); R();
                C(p.Team == 0 ? ConsoleColor.Cyan : ConsoleColor.Red);
                Console.Write(Col($"T{p.Team}", 6)); R();
                Console.Write(Col(kda, 12));
                Console.Write(Col(acc, 7));
                C(ConsoleColor.Magenta); Console.Write(Col(abilAcc, 9)); R();
                C(ConsoleColor.Red);     Console.Write(Col(dmg, 8));     R();
                C(ConsoleColor.Green);   Console.Write(Col(heal, 8));    R();
                Console.Write(Col(hits, 9));
                Console.Write(Col(p.Dashes.ToString(), 8));
                Console.Write(Col(p.AbilitiesUsed.ToString(), 9));
                Console.Write(Col(p.ShieldPickups.ToString(), 10));
                Console.Write(Col(alive, 10));
                if (isFFA)
                {
                    Console.Write(Col(p.FfaNbBackUp.ToString(), 8));
                    Console.Write(Col(p.FfaDeathRanking > 0 ? $"#{p.FfaDeathRanking}" : "-", 6));
                }
                if (isQBall)
                {
                    C(ConsoleColor.Yellow);
                    Console.Write(Col(p.Score.ToString(), 7)); R();
                }
                C(p.IsWinner ? ConsoleColor.Green : ConsoleColor.Red);
                Console.Write(result); R();
                Console.WriteLine();
            }

            Console.WriteLine();
            C(ConsoleColor.DarkGray); L($"  {Timestamp(snap)}"); R();
        }

        // ── In-match ──────────────────────────────────────────────────────────────
        static void DrawInMatch(MatchSnapshot snap)
        {
            bool isFFA   = snap.GameModeEnum == 4;
            bool isQBall = snap.GameModeEnum == 5;

            C(ConsoleColor.White);    Console.Write($"  {snap.ModeName}");
            C(ConsoleColor.DarkGray); Console.Write($"  [{snap.StateName}]");
            C(ConsoleColor.White);    Console.WriteLine($"  Time: {snap.Timer}");
            R();

            // Q-Ball: show who holds the ball
            if (isQBall && snap.QBallHolderTeam >= 0)
            {
                Console.Write("  Q-Ball held by: ");
                C(snap.QBallHolderTeam == 0 ? ConsoleColor.Cyan : ConsoleColor.Red);
                Console.WriteLine($"Team {snap.QBallHolderTeam}");
                R();
            }

            string myL    = snap.MyLives    == -1 ? "???" : snap.MyLives.ToString();
            string enemyL = snap.EnemyLives == -1 ? "???" : snap.EnemyLives.ToString();
            Console.Write("  My Team Lives: ");
            C(ConsoleColor.Cyan); Console.Write(myL.PadRight(8)); R();
            Console.Write("Enemy Lives: ");
            C(ConsoleColor.Red); Console.WriteLine(enemyL); R();
            Console.WriteLine();

            if (snap.Players.Count == 0)
            {
                C(ConsoleColor.DarkGray); L("  No players detected."); R();
                return;
            }

            var teams   = new Dictionary<int, List<PlayerInfo>>();
            foreach (var p in snap.Players)
            {
                if (!teams.ContainsKey(p.Team)) teams[p.Team] = new List<PlayerInfo>();
                teams[p.Team].Add(p);
            }
            var teamIds = new List<int>(teams.Keys);
            teamIds.Sort();

            foreach (var tid in teamIds)
            {
                C(tid == 0 ? ConsoleColor.Cyan : ConsoleColor.Red);
                L($"  TEAM {tid}"); R();

                C(ConsoleColor.DarkGray);
                L("  " + Col("Name", 20) + Col("K/D/A", 12) + Col("KD", 7) +
                  Col("Acc", 7) + Col("Weapon", 12) + Col("Ability", 18) +
                  Col("Module", 14) + Col("DMG", 8) + "HEAL");
                L("  " + Dashes(103));
                R();

                foreach (var p in teams[tid])
                {
                    string kda  = $"{p.Kills}/{p.Deaths}/{p.Assists}";
                    string kd   = p.KDRatio.ToString("F2");
                    string acc  = $"{p.Accuracy:F0}%";
                    string dmg  = p.Damage > 0 ? p.Damage.ToString("F0") : "-";
                    string heal = p.Heal > 0   ? p.Heal.ToString("F0")   : "-";

                    C(p.IsBot ? ConsoleColor.DarkGray : ConsoleColor.White);
                    Console.Write("  " + Col(p.DisplayName, 20)); R();
                    Console.Write(Col(kda, 12));
                    C(p.KDRatio >= 1 ? ConsoleColor.Green : ConsoleColor.Red);
                    Console.Write(Col(kd, 7)); R();
                    Console.Write(Col(acc, 7));
                    C(ConsoleColor.Yellow);  Console.Write(Col(p.WeaponName, 12));  R();
                    C(ConsoleColor.Magenta); Console.Write(Col(p.AbilityName, 18)); R();
                    C(ConsoleColor.Cyan);    Console.Write(Col(p.ModuleName, 14));  R();
                    C(ConsoleColor.Red);     Console.Write(Col(dmg, 8));            R();
                    C(ConsoleColor.Green);   Console.Write(heal);                   R();
                    Console.WriteLine();

                    // Extended stats row
                    C(ConsoleColor.DarkGray);
                    string ext = $"  {"".PadRight(20)}" +
                        $"Dashes:{p.Dashes,-5} " +
                        $"Jumps:{p.Jumps,-6} " +
                        $"AbilUse:{p.AbilitiesUsed,-5} " +
                        $"AbilHit:{p.NbAbilitiesHit,-5} " +
                        $"ShldPick:{p.ShieldPickups,-5} " +
                        $"Impulse(rx):{p.ImpulseReceived.ToString("F0"),-7} " +
                        $"GravCtrl:{p.GravityDuration.ToString("F0")}s/{p.GravityUseCount}x " +
                        $"Alive:{p.FormatAlive()}";
                    if (isFFA && p.FfaDeathRanking > 0)
                        ext += $"  BackUp:{p.FfaNbBackUp} Rank:#{p.FfaDeathRanking}";
                    if (isQBall && p.Score > 0)
                        ext += $"  BallScore:{p.Score}";
                    L(ext);
                    R();
                }
                Console.WriteLine();
            }

            C(ConsoleColor.DarkGray);
            L($"  {Timestamp(snap)}    Players: {snap.Players.Count}");
            R();

            if (snap.KillFeed.Count > 0) DrawKillFeed(snap.KillFeed);
        }

        // ── Kill feed ─────────────────────────────────────────────────────────────
        static void DrawKillFeed(List<KillFeedEntry> feed)
        {
            Console.WriteLine();
            C(ConsoleColor.White);   L("  KILL FEED"); R();
            C(ConsoleColor.DarkGray); L("  " + Dashes(60)); R();

            for (int i = feed.Count - 1; i >= 0; i--)
            {
                var k = feed[i];
                C(ConsoleColor.DarkGray); Console.Write($"  [{k.Time:HH:mm:ss}] ");
                C(k.KillerTeam == 0 ? ConsoleColor.Cyan
                : k.KillerTeam == 1 ? ConsoleColor.Red
                : ConsoleColor.DarkGray);
                Console.Write(k.KillerName);
                C(ConsoleColor.DarkGray); Console.Write("  [");
                C(k.IsAbilityKill ? ConsoleColor.Magenta : ConsoleColor.Yellow);
                Console.Write(k.Cause);
                C(ConsoleColor.DarkGray); Console.Write("]  ");
                C(k.KillerTeam == 0 ? ConsoleColor.Red
                : k.KillerTeam == 1 ? ConsoleColor.Cyan
                : ConsoleColor.White);
                Console.WriteLine(k.VictimName);
                R();
            }
        }

        static void C(ConsoleColor c) => Console.ForegroundColor = c;
        static void R() => Console.ResetColor();
        static void L(string s) => Console.WriteLine(s);
        static string Col(string s, int w) => s.PadRight(w);
        static string Dashes(int n) => new string('─', n);
        static string Timestamp(MatchSnapshot snap)
            => snap != null ? $"Updated: {snap.UpdatedAt:HH:mm:ss}" : "";
    }
}
