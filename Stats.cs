using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using Windows.Win32;
using Windows.Win32.System.Performance;
using Windows.Win32.System.SystemInformation;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;   // the type CsWin32 maps GetSystemTimes to

namespace TaskbarMonitor;

/// <summary>Pure maths + formatting, kept free of Win32 so --selftest can check it.</summary>
internal static class Calc
{
    static readonly CultureInfo CI = CultureInfo.InvariantCulture;

    /// <summary>
    /// Bytes moved since the previous sample, summed over adapters present in both samples.
    /// A new adapter has no baseline (skipped), a counter that went backwards counts as 0.
    /// </summary>
    public static (long Rx, long Tx) NetDelta(IReadOnlyDictionary<string, (long Rx, long Tx)> prev, IReadOnlyDictionary<string, (long Rx, long Tx)> cur)
    {
        long rx = 0, tx = 0;
        foreach (var (id, c) in cur)
        {
            if (!prev.TryGetValue(id, out var p)) continue;
            if (c.Rx >= p.Rx) rx += c.Rx - p.Rx;
            if (c.Tx >= p.Tx) tx += c.Tx - p.Tx;
        }
        return (rx, tx);
    }

    /// <summary>Busy share of CPU time. kernel already contains idle (GetSystemTimes semantics).</summary>
    public static int CpuPercent(ulong dIdle, ulong dKernel, ulong dUser)
    {
        ulong total = dKernel + dUser;
        if (total == 0 || dIdle > total) return 0;
        return (int)Math.Round(100.0 * (total - dIdle) / total);
    }

    /// <summary>"8.5 W" style power text.</summary>
    public static string Watts(double w) => (w >= 100 ? w.ToString("0", CI) : w.ToString("0.0", CI)) + " W";

    /// <summary>"12.3 MB/s" style text; bits uses decimal steps ("Mbps"), bytes binary steps.</summary>
    public static string Speed(double bytesPerSec, bool bits)
    {
        double v = bits ? bytesPerSec * 8 : bytesPerSec;
        double step = bits ? 1000 : 1024;
        string[] unit = bits ? ["bps", "Kbps", "Mbps", "Gbps"] : ["B/s", "KB/s", "MB/s", "GB/s"];
        int i = 0;
        while (v >= 1000 && i < unit.Length - 1) { v /= step; i++; }
        string num = i == 0 || v >= 100 ? v.ToString("0", CI) : v >= 10 ? v.ToString("0.0", CI) : v.ToString("0.00", CI);
        return $"{num} {unit[i]}";
    }
}

/// <summary>Samples network speed, CPU and memory once per call (call it once a second).</summary>
internal sealed unsafe class Sampler
{
    Dictionary<string, (long Rx, long Tx)> _prevNet = new();
    long _prevTicks;
    ulong _idle, _kernel, _user;
    bool _haveCpu;

    public double UpBps, DownBps;
    public int Cpu, Mem;
    public int Temp;            // degrees C of the hottest ACPI thermal zone, 0 when the machine exposes none

    PDH_HQUERY _tq; PDH_HCOUNTER _tc; bool _hasTemp;   // PDH query and counter handles for "Thermal Zone Information(*)\Temperature"

    public Sampler()
    {
        // ponytail: ACPI thermal zone, needs no driver or admin but is the board/package zone, not per-core.
        // Per-core CPU, GPU and disk temperatures need a kernel-driver sensor library (LibreHardwareMonitor) and admin.
        if (PInvoke.PdhOpenQuery(null, 0, out _tq) != 0) return;
        _hasTemp = PInvoke.PdhAddEnglishCounter(_tq, @"\Thermal Zone Information(*)\Temperature", 0, out _tc) == 0;
    }

    void SampleTemp()
    {
        if (!_hasTemp) return;
        PInvoke.PdhCollectQueryData(_tq);
        uint size = 0, count = 0;
        PInvoke.PdhGetFormattedCounterArray(_tc, PDH_FMT.PDH_FMT_DOUBLE, ref size, out count, out _);   // PDH_MORE_DATA: asks for the size
        if (size == 0) { Temp = 0; return; }
        var buf = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)size);
        try
        {
            var items = (PDH_FMT_COUNTERVALUE_ITEM_W*)buf;
            if (PInvoke.PdhGetFormattedCounterArray(_tc, PDH_FMT.PDH_FMT_DOUBLE, ref size, out count, out *items) != 0) { Temp = 0; return; }
            double max = 0;
            for (uint i = 0; i < count; i++)
                if (items[i].FmtValue.CStatus == 0) max = Math.Max(max, items[i].FmtValue.Anonymous.doubleValue);
            double c = max - 273.15;                       // the counter is in Kelvin
            Temp = c is > 0 and < 150 ? (int)Math.Round(c) : 0;
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buf); }
    }

    public readonly Battery Bat = new();
    public AdminSensors? Admin;   // set only when running elevated with the option on
    int _adminTick;

    public void Sample()
    {
        if (Admin != null && _adminTick++ % 3 == 0) Admin.Update();   // sensor reads are slow, every 3 s is plenty
        SampleNet();
        SampleCpu();
        SampleTemp();
        Bat.Update();
        MEMORYSTATUSEX m = default;
        m.dwLength = (uint)sizeof(MEMORYSTATUSEX);
        if (PInvoke.GlobalMemoryStatusEx(&m)) Mem = (int)m.dwMemoryLoad;
    }

    // Listing adapters costs 35-70 ms on a machine with many filter layers, reading one adapter's counters is ~0.1 ms.
    // So pick the adapters rarely and read their counters every tick.
    const int PickEveryTicks = 15;
    List<NetworkInterface> _nics = [];
    int _ticksSincePick;
    bool _needPick = true;

    internal int NicCount => _nics.Count;

    void SampleNet()
    {
        var cur = new Dictionary<string, (long, long)>();
        try
        {
            if (_needPick || ++_ticksSincePick >= PickEveryTicks) { _nics = PickNics(); _ticksSincePick = 0; _needPick = false; }
            foreach (var nic in _nics)
            {
                var s = nic.GetIPStatistics();   // fresh counters on every call
                cur[nic.Id] = (s.BytesReceived, s.BytesSent);
            }
        }
        catch (NetworkInformationException) { _needPick = true; return; }   // adapter vanished, re-pick next tick

        long now = Environment.TickCount64;
        if (_prevTicks != 0 && now > _prevTicks)
        {
            double sec = (now - _prevTicks) / 1000.0;
            var (rx, tx) = Calc.NetDelta(_prevNet, cur);
            DownBps = rx / sec;
            UpBps = tx / sec;
        }
        _prevNet = cur;
        _prevTicks = now;
    }

    internal static List<NetworkInterface> PickNics()
    {
        // Windows also lists filter layers ("Ethernet-WFP ...", "...-QoS Packet Scheduler") and WAN miniports
        // that mirror the real adapter's counters. Only adapters with a default gateway carry real traffic.
        // ponytail: sums every such adapter; VPN adapters with a gateway may double count, add a picker in config if so.
        return NetworkInterface.GetAllNetworkInterfaces().Where(nic =>
            nic.OperationalStatus == OperationalStatus.Up
            && nic.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            && !IsVirtual(nic.Description) && HasGateway(nic)).ToList();
    }

    static bool HasGateway(NetworkInterface nic) =>
        nic.GetIPProperties().GatewayAddresses.Any(g => !IPAddress.Any.Equals(g.Address) && !IPAddress.IPv6Any.Equals(g.Address));

    static bool IsVirtual(string d) =>
        d.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || d.Contains("vEthernet", StringComparison.OrdinalIgnoreCase) ||
        d.Contains("VMware", StringComparison.OrdinalIgnoreCase) || d.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) ||
        d.Contains("TAP-", StringComparison.OrdinalIgnoreCase) || d.Contains("WireGuard", StringComparison.OrdinalIgnoreCase) ||
        d.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase);

    void SampleCpu()
    {
        FILETIME i, k, u;
        if (!PInvoke.GetSystemTimes(&i, &k, &u)) return;
        ulong idle = Ft(i), kernel = Ft(k), user = Ft(u);
        if (_haveCpu) Cpu = Calc.CpuPercent(idle - _idle, kernel - _kernel, user - _user);
        (_idle, _kernel, _user, _haveCpu) = (idle, kernel, user, true);
    }

    static ulong Ft(FILETIME f) => ((ulong)(uint)f.dwHighDateTime << 32) | (uint)f.dwLowDateTime;
}


