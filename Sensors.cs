using System;
using System.ComponentModel;
using System.Diagnostics;
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
