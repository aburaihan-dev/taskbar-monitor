using System;
using System.Collections.Generic;
using System.Threading;

namespace TaskbarMonitor;

internal static class Program
{
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--selftest") return SelfTest();

        // An elevated relaunch starts while the old copy is still shutting down, so it may wait a few seconds for the mutex.
        using var single = new Mutex(false, @"Local\TaskbarMonitor.SingleInstance");
        bool got;
        try { got = single.WaitOne(Array.IndexOf(args, "--relaunch") >= 0 ? 8000 : 0); }
        catch (AbandonedMutexException) { got = true; }
        if (!got) return 0;

        new TaskbarWindow(Config.Load()).Run();
        return 0;
    }

    // Smallest check that fails if the maths breaks. Run: dotnet run -p:Selftest=true -- --selftest
    static int SelfTest()
    {
        int fails = 0;
        void Eq<T>(T want, T got, string what)
        {
            if (!EqualityComparer<T>.Default.Equals(want, got)) { Console.WriteLine($"FAIL {what}: want {want}, got {got}"); fails++; }
        }

        Eq("0 B/s", Calc.Speed(0, false), "speed zero");
        Eq("512 B/s", Calc.Speed(512, false), "speed bytes");
        Eq("1.50 KB/s", Calc.Speed(1536, false), "speed KB");
        Eq("12.3 MB/s", Calc.Speed(12.34 * 1024 * 1024, false), "speed MB 1 decimal");
        Eq("123 MB/s", Calc.Speed(123.4 * 1024 * 1024, false), "speed MB 0 decimals");
        Eq("0.98 KB/s", Calc.Speed(1000, false), "speed rolls over at 1000");
        Eq("1.00 Mbps", Calc.Speed(125000, true), "speed bits");

        var prev = new Dictionary<string, (long Rx, long Tx)> { ["a"] = (100, 200) };
        Eq((50L, 60L), Calc.NetDelta(prev, new Dictionary<string, (long Rx, long Tx)> { ["a"] = (150, 260), ["new"] = (999, 999) }), "delta ignores new adapter");
        Eq((0L, 0L), Calc.NetDelta(prev, new Dictionary<string, (long Rx, long Tx)> { ["a"] = (10, 20) }), "delta counter reset is 0");
        Eq((0L, 0L), Calc.NetDelta(prev, new Dictionary<string, (long Rx, long Tx)>()), "delta adapter gone");

        Eq(75, Calc.CpuPercent(50, 100, 100), "cpu 75%");
        Eq(0, Calc.CpuPercent(0, 0, 0), "cpu no time elapsed");
        Eq(100, Calc.CpuPercent(0, 10, 10), "cpu fully busy");

        // live sample must not throw and must give sane ranges
        var s = new Sampler();
        s.Sample(); Thread.Sleep(300); s.Sample();
        Eq(Sampler.PickNics().Count, s.NicCount, "sampler picked the same adapters a fresh pick finds");
        if (s.Mem is < 1 or > 100 || (s.Temp != 0 && s.Temp is < 10 or > 130) || s.Cpu is < 0 or > 100 || s.UpBps < 0 || s.DownBps < 0) { Console.WriteLine($"FAIL live sample: cpu={s.Cpu} mem={s.Mem} up={s.UpBps} down={s.DownBps}"); fails++; }

        Console.WriteLine(fails == 0 ? "PASS" : $"{fails} FAILED");
        return fails == 0 ? 0 : 1;
    }
}
