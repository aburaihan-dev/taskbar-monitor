using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;

namespace TaskbarMonitor;

/// <summary>
/// Optional GPU and disk temperatures. Only GPU and storage are enabled (no CPU/mainboard), because those two
/// need administrator rights but no kernel driver. Created only when the user turns it on while elevated,
/// so the library is never loaded otherwise.
/// </summary>
internal sealed class AdminSensors : IDisposable
{
    readonly Computer _pc = new() { IsGpuEnabled = true, IsStorageEnabled = true };

    public int Gpu, Disk;   // degrees C, 0 = no reading

    public AdminSensors() => _pc.Open();

    public void Update()
    {
        int gpu = 0, disk = 0;
        foreach (var hw in _pc.Hardware)
        {
            bool isGpu = hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;
            if (!isGpu && hw.HardwareType != HardwareType.Storage) continue;
            hw.Update();
            foreach (var s in hw.Sensors)
            {
                if (s.SensorType != SensorType.Temperature || s.Value is not float v || v <= 0 || v >= 150) continue;
                if (isGpu) gpu = Math.Max(gpu, (int)Math.Round(v)); else disk = Math.Max(disk, (int)Math.Round(v));
            }
        }
        (Gpu, Disk) = (gpu, disk);
    }

    public void Dispose() => _pc.Close();
}

internal static class Admin
{
    public static bool IsElevated
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>Starts a new elevated copy (UAC prompt). False when the user declines.</summary>
    public static bool RelaunchElevated()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--relaunch") { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (Win32Exception) { return false; }   // UAC prompt declined
    }
}

/// <summary>
/// Battery state and charge/discharge power through the battery class driver (IOCTL_BATTERY_QUERY_STATUS).
/// Needs no admin and no extra package. Uses the first battery; present stays false on desktops.
/// </summary>
internal sealed unsafe class Battery : IDisposable
{
    const uint IoctlQueryTag = 0x294040, IoctlQueryStatus = 0x29404C;
    const uint PowerOnLine = 1, Discharging = 2, Charging = 4;
    const uint UnknownRate = 0x80000000;

    [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiGetClassDevsW(ref Guid g, IntPtr e, IntPtr h, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid g, uint i, byte* data);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, byte* data, byte* detail, uint size, out uint req, IntPtr info);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool DeviceIoControl(IntPtr h, uint code, void* inBuf, uint inSize, void* outBuf, uint outSize, out uint ret, IntPtr ov);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    IntPtr _h = new(-1);
    int _ticksSinceFind = 100;

    public bool Present;
    public bool OnAc, IsCharging, IsDischarging;
    public double Watts;   // always >= 0, direction is in IsCharging / IsDischarging

    public void Update()
    {
        Present = false;
        if (_h == new IntPtr(-1))
        {
            if (++_ticksSinceFind < 15) return;     // a missing battery is re-checked only every 15 samples
            _ticksSinceFind = 0;
            Open();
            if (_h == new IntPtr(-1)) return;
        }
        uint wait = 0, tag = 0, ret;
        if (!DeviceIoControl(_h, IoctlQueryTag, &wait, 4, &tag, 4, out ret, IntPtr.Zero) || tag == 0) { Close(); return; }
        uint* w = stackalloc uint[5] { tag, 0, 0, 0, 0 };   // BATTERY_WAIT_STATUS: tag, timeout, power state, low/high capacity
        uint* st = stackalloc uint[4];                        // BATTERY_STATUS: power state, capacity, voltage, rate
        if (!DeviceIoControl(_h, IoctlQueryStatus, w, 20, st, 16, out ret, IntPtr.Zero)) { Close(); return; }
        Present = true;
        (OnAc, IsCharging, IsDischarging) = ((st[0] & PowerOnLine) != 0, (st[0] & Charging) != 0, (st[0] & Discharging) != 0);
        Watts = st[3] == UnknownRate ? 0 : Math.Abs((int)st[3]) / 1000.0;   // mW, negative while discharging
    }

    void Open()
    {
        var guid = new Guid("72631e54-78a4-11d0-bcf7-00aa00b7b32a");   // GUID_DEVICE_BATTERY
        var set = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, 0x12);   // DIGCF_PRESENT | DIGCF_DEVICEINTERFACE
        if (set == new IntPtr(-1)) return;
        try
        {
            byte* data = stackalloc byte[32];                 // SP_DEVICE_INTERFACE_DATA on x64
            *(uint*)data = 32;
            if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, 0, data)) return;   // no battery
            SetupDiGetDeviceInterfaceDetailW(set, data, null, 0, out uint need, IntPtr.Zero);   // asks for the size
            if (need == 0) return;
            byte* detail = stackalloc byte[(int)need];
            *(uint*)detail = 8;                               // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W on x64
            if (!SetupDiGetDeviceInterfaceDetailW(set, data, detail, need, out _, IntPtr.Zero)) return;
            _h = CreateFileW(new string((char*)(detail + 4)), 0xC0000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);   // read+write, OPEN_EXISTING
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    void Close() { if (_h != new IntPtr(-1)) CloseHandle(_h); _h = new IntPtr(-1); }

    public void Dispose() => Close();
}
