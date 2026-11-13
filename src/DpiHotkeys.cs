using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace AeroxDpi
{
    internal sealed class DpiHotkeys : IDisposable
    {
        internal const int Message = 0x0312;
        private const int NextId = 4100;
        private const int PreviousId = 4101;
        private const int FirstPresetId = 4102;
        private const uint Modifiers = 0x0001 | 0x0002 | 0x4000;
        private readonly IntPtr window;
        private readonly HashSet<int> registered = new HashSet<int>();
        private readonly string[] unavailable;
        private bool disposed;

        internal DpiHotkeys(IntPtr window)
        {
            if (window == IntPtr.Zero)
                throw new ArgumentException("A window handle is required.", "window");
            this.window = window;
            var failures = new List<string>();
            try
            {
                Register(NextId, 0x26, "Ctrl+Alt+Up", failures);
                Register(PreviousId, 0x28, "Ctrl+Alt+Down", failures);
                for (int preset = 0; preset < 5; preset++)
                    Register(FirstPresetId + preset, (uint)(0x31 + preset),
                        "Ctrl+Alt+" + (preset + 1), failures);
                unavailable = failures.ToArray();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal string ConflictDescription
        {
            get { return String.Join(", ", unavailable); }
        }

        internal string[] UnavailableShortcuts
        {
            get { return (string[])unavailable.Clone(); }
        }

        private void Register(int id, uint key, string name, List<string> failures)
        {
            if (RegisterHotKey(window, id, Modifiers, key))
                registered.Add(id);
            else
                failures.Add(name);
        }

        internal bool TryResolve(int id, int currentIndex, int presetCount, out int index)
        {
            index = -1;
            return !disposed && registered.Contains(id)
                && TryResolveId(id, currentIndex, presetCount, out index);
        }

        // Pure mapping is separate so tests never register system shortcuts.
        internal static bool TryResolveId(int id, int currentIndex, int presetCount, out int index)
        {
            index = -1;
            if (presetCount <= 0)
                return false;
            bool known = currentIndex >= 0 && currentIndex < presetCount;
            if (id == NextId)
            {
                index = !known || currentIndex == presetCount - 1 ? 0 : currentIndex + 1;
                return true;
            }
            if (id == PreviousId)
            {
                index = !known || currentIndex == 0 ? presetCount - 1 : currentIndex - 1;
                return true;
            }
            if (id >= FirstPresetId && id < FirstPresetId + 5)
            {
                int preset = id - FirstPresetId;
                if (preset < presetCount)
                {
                    index = preset;
                    return true;
                }
            }
            return false;
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            foreach (int id in registered)
            {
                try { UnregisterHotKey(window, id); }
                catch (Exception) { /* Cleanup must not interrupt window shutdown. */ }
            }
            registered.Clear();
        }

        // MOD_NOREPEAT suppresses keyboard autorepeat; failed registrations stay inactive.
        // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}
