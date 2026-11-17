using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace TaskbarMonitor;

internal sealed class Config
{
    public bool ShowUp { get; set; } = true;
    public bool ShowDown { get; set; } = true;
    public bool ShowCpu { get; set; } = true;
    public bool ShowMemory { get; set; } = true;
    public bool ShowTemp { get; set; } = true;      // hidden automatically when the machine has no thermal zone
    public bool ShowBattery { get; set; } = true;   // charge state and power draw, hidden automatically on machines without a battery
    public bool AdminSensors { get; set; }          // GPU/disk temperatures, needs the app to run as administrator
    public int RefreshMs { get; set; } = 1000;      // how often everything is sampled and redrawn
    public bool Bits { get; set; }            // bit/s instead of byte/s
    public float FontPx { get; set; } = 12;         // font height in pixels at 100% scale
    public int OffsetX { get; set; }                // px at 100% scale, positive moves right
    public string TextColor { get; set; } = "auto"; // "auto" follows light/dark, or "#RRGGBB"

    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskbarMonitor", "config.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Config Load()
    {
        Config c;
        try { c = JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { c = new(); }
        if (!c.ShowUp && !c.ShowDown && !c.ShowCpu && !c.ShowMemory && !c.ShowTemp) c.ShowUp = c.ShowDown = c.ShowCpu = c.ShowMemory = c.ShowTemp = true;
        c.FontPx = Math.Clamp(c.FontPx, 8, 24);
        c.RefreshMs = Math.Clamp(c.RefreshMs, 250, 60000);
        return c;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* settings just won't persist */ }
    }
}

internal static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "TaskbarMonitor";

    public static bool IsOn
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(Name) is string;
        }
    }

    public static void Set(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(Name, $"\"{Environment.ProcessPath}\"");
        else k.DeleteValue(Name, throwOnMissingValue: false);
    }
}
