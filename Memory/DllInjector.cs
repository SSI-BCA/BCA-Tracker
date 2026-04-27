using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace BCATracker
{
    /// <summary>
    /// Injects BCAFNameResolver.dll into BattleCoreArena.exe automatically.
    /// Called once per session by Program.cs after attaching to the process.
    /// The DLL resolves FName indices, writes fnames.json, and unloads itself.
    /// The user never sees any indication this happened.
    /// </summary>
    public static class DllInjector
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr addr, uint size, uint type, uint protect);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, uint size, out int written);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateRemoteThread(IntPtr h, IntPtr attr, uint stack,
            IntPtr start, IntPtr param, uint flags, out uint tid);
        [DllImport("kernel32.dll")]
        static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")]
        static extern IntPtr GetProcAddress(IntPtr mod, string name);
        [DllImport("kernel32.dll")]
        static extern IntPtr GetModuleHandle(string name);

        const int PROCESS_ALL_ACCESS   = 0x1F0FFF;
        const uint MEM_COMMIT_RESERVE  = 0x3000;
        const uint PAGE_READWRITE      = 0x04;

        /// <summary>
        /// Injects BCAFNameResolver.dll into the given process.
        /// The DLL path is resolved relative to the tracker executable.
        /// Returns true if injection was successful.
        /// </summary>
        public static bool Inject(int pid)
        {
            string dllPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "BCAFNameResolver.dll");

            if (!File.Exists(dllPath)) return false;

            string fullPath = Path.GetFullPath(dllPath);
            byte[] pathBytes = Encoding.Unicode.GetBytes(fullPath + "\0");

            IntPtr hProcess = OpenProcess(PROCESS_ALL_ACCESS, false, pid);
            if (hProcess == IntPtr.Zero) return false;

            try
            {
                // Allocate memory for the DLL path string in the target process
                IntPtr remoteMem = VirtualAllocEx(hProcess, IntPtr.Zero,
                    (uint)pathBytes.Length, MEM_COMMIT_RESERVE, PAGE_READWRITE);
                if (remoteMem == IntPtr.Zero) return false;

                // Write the DLL path
                if (!WriteProcessMemory(hProcess, remoteMem, pathBytes,
                    (uint)pathBytes.Length, out _)) return false;

                // Get LoadLibraryW address (same in all 64-bit processes)
                IntPtr loadLib = GetProcAddress(GetModuleHandle("kernel32.dll"), "LoadLibraryW");
                if (loadLib == IntPtr.Zero) return false;

                // Create remote thread that calls LoadLibraryW(dllPath)
                IntPtr hThread = CreateRemoteThread(hProcess, IntPtr.Zero, 0,
                    loadLib, remoteMem, 0, out _);
                if (hThread == IntPtr.Zero) return false;

                // Wait up to 5 seconds for the DLL to load and do its work
                WaitForSingleObject(hThread, 5000);
                CloseHandle(hThread);

                return true;
            }
            finally
            {
                CloseHandle(hProcess);
            }
        }

        /// <summary>
        /// Injects the resolver and waits briefly for fnames.json to be written.
        /// Call this once after attaching to the game process.
        /// </summary>
        public static void InjectAndWait(int pid)
        {
            if (!Inject(pid)) return;

            // Give the DLL time to resolve FNames and write the file
            // (it sleeps 2s internally before resolving, then takes ~1s to scan GObjects)
            Thread.Sleep(4000);
        }
    }
}
