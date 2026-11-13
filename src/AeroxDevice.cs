using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace AeroxDpi
{
    public static class AeroxDevice
    {
        public static readonly int[] Presets = { 700, 800, 900, 1000, 1100 };
        private const int MaxReportLength = 4096;
        private static readonly object WriteLock = new object();

        // Protocol source (only sensitivity is sent; no save command):
        // https://github.com/flozz/rivalcfg/blob/master/rivalcfg/devices/aerox5.py
        // https://github.com/flozz/rivalcfg/blob/master/rivalcfg/handlers/multidpi_range_choice.py
        // https://github.com/flozz/rivalcfg/blob/master/rivalcfg/devices/dpi/truemove_air.py
        // Windows adds report ID 00. Payload: command 2D, count 05,
        // zero-based selected preset, then the five TrueMove Air values.
        public static byte[] BuildReport(int index, int reportLength)
        {
            if (index < 0 || index >= 5)
                throw new ArgumentOutOfRangeException("index", "Choose a DPI preset from 700 to 1100.");
            if (reportLength < 9 || reportLength > MaxReportLength)
                throw new ArgumentOutOfRangeException("reportLength", "Unsupported mouse output report length.");
            byte[] report = new byte[reportLength];
            byte[] command = { 0x00, 0x2D, 0x05, (byte)index, 0x07, 0x09, 0x0A, 0x0B, 0x0C };
            Array.Copy(command, report, command.Length);
            return report;
        }

        // Enumeration only: does not open or write the device.
        public static bool IsConnected()
        {
            return FindPaths().Count != 0;
        }

        public static void SelectPreset(int zeroBasedIndex)
        {
            BuildReport(zeroBasedIndex, 9); // Validate before opening hardware.
            lock (WriteLock)
            {
                List<string> paths = FindPaths();
                if (paths.Count == 0)
                    throw new IOException("Connect the wired SteelSeries Aerox 5 mouse.");
                string lastError = "No compatible output report was found.";
                foreach (string path in paths)
                {
                    SafeFileHandle handle = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
                    if (handle.IsInvalid)
                    {
                        lastError = "Cannot open the mouse: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                        handle.Dispose();
                        continue;
                    }
                    int length;
                    try { length = GetReportLength(handle); }
                    catch (Exception ex)
                    {
                        handle.Dispose();
                        lastError = ex.Message;
                        continue;
                    }
                    // WriteBounded owns this handle, including cancellation cleanup.
                    WriteBounded(handle, BuildReport(zeroBasedIndex, length));
                    return;
                }
                throw new IOException(lastError);
            }
        }

        private static int GetReportLength(SafeFileHandle handle)
        {
            IntPtr data;
            if (!HidD_GetPreparsedData(handle, out data))
                throw new IOException("Cannot read the mouse HID capabilities.");
            try
            {
                HidCaps caps;
                if (HidP_GetCaps(data, out caps) != 0x00110000)
                    throw new IOException("Cannot decode the mouse HID capabilities.");
                if (caps.OutputReportByteLength < 9 || caps.OutputReportByteLength > MaxReportLength)
                    throw new IOException("The mouse interface has an unsupported output report length.");
                return caps.OutputReportByteLength;
            }
            finally { HidD_FreePreparsedData(data); }
        }

        private sealed class PendingWrite : IDisposable
        {
            public SafeFileHandle Handle;
            public IntPtr Buffer;
            public IntPtr Overlapped;
            public EventWaitHandle Event;
            public void Dispose()
            {
                if (Overlapped != IntPtr.Zero) Marshal.FreeHGlobal(Overlapped);
                if (Buffer != IntPtr.Zero) Marshal.FreeHGlobal(Buffer);
                if (Event != null) Event.Dispose();
                Handle.Dispose();
            }
        }

        private static void WriteBounded(SafeFileHandle handle, byte[] report)
        {
            PendingWrite state = new PendingWrite { Handle = handle };
            bool deferredCleanup = false;
            try
            {
                state.Event = new EventWaitHandle(false, EventResetMode.ManualReset);
                NativeOverlappedData overlapped = new NativeOverlappedData();
                overlapped.Event = state.Event.SafeWaitHandle.DangerousGetHandle();
                state.Overlapped = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeOverlappedData)));
                Marshal.StructureToPtr(overlapped, state.Overlapped, false);
                state.Buffer = Marshal.AllocHGlobal(report.Length);
                Marshal.Copy(report, 0, state.Buffer, report.Length);
                uint written;
                bool completed = WriteFile(handle, state.Buffer, (uint)report.Length, out written, state.Overlapped);
                if (!completed)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error != 997)
                        throw new IOException("Cannot send DPI to the mouse: " + new Win32Exception(error).Message);
                    if (!state.Event.WaitOne(2000))
                    {
                        CancelIoEx(handle, state.Overlapped);
                        // The kernel may still reference the native buffers after cancellation.
                        // Retain them until completion on a background worker, without blocking callers.
                        deferredCleanup = true;
                        ThreadPool.QueueUserWorkItem(delegate
                        {
                            try { uint ignored; GetOverlappedResult(state.Handle, state.Overlapped, out ignored, true); }
                            finally { state.Dispose(); }
                        });
                        throw new IOException("The mouse did not respond in time. DPI may have changed; reconnect before retrying.");
                    }
                    if (!GetOverlappedResult(handle, state.Overlapped, out written, false))
                        throw new IOException("The mouse rejected the DPI write: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
                }
                if (written != report.Length)
                    throw new IOException("The mouse received an incomplete DPI report. Reconnect before retrying.");
                // Successful transport does not establish device readback or persistence.
            }
            finally { if (!deferredCleanup) state.Dispose(); }
        }

        private static List<string> FindPaths()
        {
            Guid hid;
            HidD_GetHidGuid(out hid);
            IntPtr list = SetupDiGetClassDevs(ref hid, null, IntPtr.Zero, 0x12);
            if (list == new IntPtr(-1))
                throw new IOException("Cannot enumerate HID devices: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
            List<string> paths = new List<string>();
            try
            {
                for (uint index = 0; ; index++)
                {
                    InterfaceData item = new InterfaceData();
                    item.Size = Marshal.SizeOf(typeof(InterfaceData));
                    if (!SetupDiEnumDeviceInterfaces(list, IntPtr.Zero, ref hid, index, ref item))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error == 259) break;
                        throw new IOException("Cannot enumerate the mouse interface: " + new Win32Exception(error).Message);
                    }
                    uint required;
                    SetupDiGetDeviceInterfaceDetail(list, ref item, IntPtr.Zero, 0, out required, IntPtr.Zero);
                    if (required < 8 || required > 65536) continue;
                    IntPtr detail = Marshal.AllocHGlobal((int)required);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(list, ref item, detail, required, out required, IntPtr.Zero)) continue;
                        string path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                        if (path == null) continue;
                        string lower = path.ToLowerInvariant();
                        const string identity = "#vid_1038&pid_1850&mi_03";
                        int start = lower.IndexOf(identity, StringComparison.Ordinal);
                        int end = start + identity.Length;
                        if (start >= 0 && (end == lower.Length || lower[end] == '&' || lower[end] == '#'))
                            paths.Add(path);
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(list); }
            return paths;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct InterfaceData
        {
            public int Size;
            public Guid InterfaceClassGuid;
            public uint Flags;
            public IntPtr Reserved;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct HidCaps
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort LinkCollectionNodes, InputButtonCaps, InputValueCaps, InputDataIndices;
            public ushort OutputButtonCaps, OutputValueCaps, OutputDataIndices;
            public ushort FeatureButtonCaps, FeatureValueCaps, FeatureDataIndices;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeOverlappedData
        {
            public UIntPtr Internal, InternalHigh;
            public uint Offset, OffsetHigh;
            public IntPtr Event;
        }
        [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, out HidCaps caps);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr list, IntPtr device, ref Guid guid, uint index, ref InterfaceData data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr list, ref InterfaceData data, IntPtr detail, uint size, out uint required, IntPtr device);
        [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr list);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteFile(SafeFileHandle handle, IntPtr buffer, uint count, out uint written, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetOverlappedResult(SafeFileHandle handle, IntPtr overlapped, out uint transferred, bool wait);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);
    }
}
