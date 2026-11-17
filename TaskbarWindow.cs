using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace TaskbarMonitor;

/// <summary>
/// A layered child window of Shell_TrayWnd drawn with per-pixel alpha, placed left of the tray.
/// Windows 11 paints the taskbar in a full-width XAML bridge window that is the topmost child,
/// so the widget has to be re-raised above it (see Refresh).
/// </summary>
internal sealed unsafe class TaskbarWindow
{
    const string Cls = "TaskbarMonitorWidget";
    const string ThemeKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    const int GapPx = 4, ColGapPx = 12, PadPx = 2;   // at 100% scale
    static readonly int[] FontSizes = [10, 11, 12, 13, 14, 16];
    const uint TpmRightButton = 0x0002, TpmBottomAlign = 0x0020, TpmReturnCmd = 0x0100;   // TrackPopupMenuEx flags (CsWin32 leaves them as plain uint)

    // menu command ids
    const int IdUp = 1, IdDown = 2, IdCpu = 3, IdMem = 4, IdBits = 5, IdTemp = 6, IdAdmin = 7, IdBat = 8, IdRate = 40;
    static readonly int[] RatesMs = [500, 1000, 2000, 5000];
    const int IdFont = 10, IdLeft = 20, IdRight = 21, IdReset = 22, IdAuto = 30, IdExit = 99;

    readonly Config _cfg;
    readonly Sampler _stats = new();
    readonly HINSTANCE _hinst = (HINSTANCE)PInvoke.GetModuleHandle((string?)null);   // Marshal.GetHINSTANCE returns -1 in single-file apps
    readonly WNDPROC _proc;
    HWND _owner, _wnd;                    // _owner: hidden top-level window that owns the context menu
    int _x = int.MinValue, _y, _w, _h;    // last applied placement

    readonly record struct Row(string Label, string Value, string Sample);

    public TaskbarWindow(Config cfg)
    {
        _cfg = cfg;
        _proc = WndProc;
        if (cfg.AdminSensors) StartAdminSensors();
    }

    public void Run()
    {
        fixed (char* cls = Cls)
        {
            var wc = new WNDCLASSEXW { cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(), lpfnWndProc = _proc, hInstance = _hinst, lpszClassName = new PCWSTR(cls) };
            PInvoke.RegisterClassEx(in wc);
        }
        _owner = PInvoke.CreateWindowEx(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW, Cls, "", WINDOW_STYLE.WS_POPUP, 0, 0, 0, 0, HWND.Null, HMENU.Null, _hinst, null);

        // A thread timer (no window) keeps ticking when explorer dies and takes the widget with it.
        StartTimer();
        Tick();
        while (PInvoke.GetMessage(out var msg, HWND.Null, 0, 0))
        {
            if (msg.message == PInvoke.WM_TIMER && msg.hwnd.IsNull) { if ((nuint)msg.wParam.Value == _timerId) Tick(); continue; }
            PInvoke.TranslateMessage(in msg);
            PInvoke.DispatchMessage(in msg);
        }
        if (PInvoke.IsWindow(_wnd)) PInvoke.DestroyWindow(_wnd);
        PInvoke.DestroyWindow(_owner);
    }

    LRESULT WndProc(HWND h, uint msg, WPARAM wp, LPARAM lp)
    {
        if (msg == PInvoke.WM_RBUTTONUP && h == _wnd) { ShowMenu(); return (LRESULT)0; }
        return PInvoke.DefWindowProc(h, msg, wp, lp);
    }

    nuint _timerId;

    void StartTimer()
    {
        if (_timerId != 0) PInvoke.KillTimer(HWND.Null, _timerId);
        _timerId = PInvoke.SetTimer(HWND.Null, 0, (uint)_cfg.RefreshMs, null);   // thread timer: the returned id is the real one
    }

    void StartAdminSensors()
    {
        if (!TaskbarMonitor.Admin.IsElevated || _stats.Admin != null) return;
        try { _stats.Admin = new AdminSensors(); }
        catch (Exception) { _stats.Admin = null; }   // the sensor library can throw on odd hardware; the widget must keep running without it
    }

    void StopAdminSensors()
    {
        _stats.Admin?.Dispose();
        _stats.Admin = null;
    }

    Font? _font;
    float _fontPx;
    Graphics? _meas;
    string _layoutKey = "", _drawKey = "";
    List<(float LabelW, float ValueW)> _cols = [];
    int _ticks;
    static readonly StringFormat Fmt = new(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap };

    static Graphics MakeMeasure()
    {
        var g = Graphics.FromHwnd(IntPtr.Zero);
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        return g;
    }

    void Tick()
    {
        _stats.Sample();
        Refresh();
        // Idle footprint: every 20 s collect the tiny heap and hand unused pages back to Windows.
        if (++_ticks % 20 == 0)
        {
            GC.Collect();
            PInvoke.SetProcessWorkingSetSize(PInvoke.GetCurrentProcess(), nuint.MaxValue, nuint.MaxValue);
        }
    }

    /// <summary>(Re)attach to the taskbar if needed, then redraw and place the widget.</summary>
    void Refresh()
    {
        var tray = PInvoke.FindWindow("Shell_TrayWnd", null);
        if (tray.IsNull) return;                         // explorer is restarting, try again next tick

        if (!PInvoke.IsWindow(_wnd))                     // first run, or explorer restarted and destroyed it
        {
            _wnd = PInvoke.CreateWindowEx(
                WINDOW_EX_STYLE.WS_EX_LAYERED | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE,
                Cls, "TaskbarMonitor", WINDOW_STYLE.WS_CHILD | WINDOW_STYLE.WS_VISIBLE | WINDOW_STYLE.WS_CLIPSIBLINGS,
                0, 0, 1, 1, tray, HMENU.Null, _hinst, null);
            _x = int.MinValue;
        }
        else if (PInvoke.GetParent(_wnd) != tray)
        {
            PInvoke.SetParent(_wnd, tray);
            _x = int.MinValue;
        }
        if (_wnd.IsNull) return;

        float s = PInvoke.GetDpiForWindow(tray) / 96f;
        var groups = Groups();
        // Font, measuring surface and column widths are cached; they only change with scale, font size or enabled items.
        float fpx = _cfg.FontPx * s;
        if (_font == null || _fontPx != fpx)
        {
            _font?.Dispose();
            _font = new Font("Segoe UI", fpx, FontStyle.Regular, GraphicsUnit.Pixel);
            _fontPx = fpx;
        }
        var font = _font;
        var meas = _meas ??= MakeMeasure();
        var fmt = Fmt;
        float Mw(string t) => meas.MeasureString(t, font, int.MaxValue, fmt).Width;

        int gap = (int)(GapPx * s), colGap = (int)(ColGapPx * s), pad = (int)(PadPx * s), rowH = font.Height;
        string layoutKey = fpx + ":" + string.Join("|", groups.Select(g => string.Concat(g.Select(r => r.Label + r.Sample))));
        if (layoutKey != _layoutKey)
        {
            _cols = groups.Select(g => (LabelW: g.Max(r => Mw(r.Label)), ValueW: g.Max(r => Mw(r.Sample)))).ToList();
            _layoutKey = layoutKey;
        }
        var cols = _cols;
        int w = pad * 2 + colGap * (cols.Count - 1) + cols.Sum(c => (int)Math.Ceiling(c.LabelW + gap + c.ValueW));
        int h = pad * 2 + rowH * 2;

        // placement: left of the tray area, vertically centred on the taskbar
        PInvoke.GetWindowRect(tray, out var rt);
        int nx = 0;
        var notify = PInvoke.FindWindowEx(tray, HWND.Null, "TrayNotifyWnd", null);
        if (!notify.IsNull && PInvoke.GetWindowRect(notify, out var rn)) nx = rn.left - rt.left;
        if (nx <= 0) nx = rt.right - rt.left - (int)(88 * s);       // tray rect unavailable: assume the clock area width
        int x = nx - w + 2 + (int)(_cfg.OffsetX * s);
        int y = (rt.bottom - rt.top - h) / 2;

        bool raise = PInvoke.GetWindow(tray, GET_WINDOW_CMD.GW_CHILD) != _wnd;
        bool moved = raise || x != _x || y != _y || w != _w || h != _h;
        if (moved)
        {
            PInvoke.SetWindowPos(_wnd, HWND.Null /* HWND_TOP */, x, y, w, h, SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
            (_x, _y, _w, _h) = (x, y, w, h);
        }

        // Nothing visible changed (same text, colour and placement): skip the whole redraw.
        var color = TextColor();
        string drawKey = layoutKey + color.ToArgb() + string.Join("|", groups.Select(g => string.Concat(g.Select(r => r.Value + ";"))));
        if (!moved && drawKey == _drawKey) return;
        _drawKey = drawKey;

        // Draw into a DIB section that a premultiplied GDI+ bitmap wraps, so alpha is exact.
        var bmi = new BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
        bmi.bmiHeader.biWidth = w; bmi.bmiHeader.biHeight = -h; bmi.bmiHeader.biPlanes = 1; bmi.bmiHeader.biBitCount = 32;
        void* bits;
        var screen = PInvoke.GetDC(HWND.Null);
        var mem = PInvoke.CreateCompatibleDC(screen);
        var hbm = PInvoke.CreateDIBSection(screen, &bmi, DIB_USAGE.DIB_RGB_COLORS, &bits, HANDLE.Null, 0);
        var old = PInvoke.SelectObject(mem, hbm);
        try
        {
            using (var bmp = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, (IntPtr)bits))
            using (var g = Graphics.FromImage(bmp))
            using (var brush = new SolidBrush(color))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                g.Clear(Color.FromArgb(1, 0, 0, 0));      // alpha 1 (not 0) so clicks on the gaps still reach us
                int cx = pad;
                for (int ci = 0; ci < cols.Count; ci++)
                {
                    var rows = groups[ci];
                    int colW = (int)Math.Ceiling(cols[ci].LabelW + gap + cols[ci].ValueW);
                    int y0 = pad + (2 - rows.Count) * rowH / 2;     // a lone row is centred
                    for (int i = 0; i < rows.Count; i++)
                    {
                        var r = rows[i];
                        float ry = y0 + i * rowH;
                        g.DrawString(r.Label, font, brush, cx, ry, fmt);
                        g.DrawString(r.Value, font, brush, cx + colW - Mw(r.Value), ry, fmt);
                    }
                    cx += colW + colGap;
                }
            }
            var size = new SIZE { cx = w, cy = h };
            var src = new Point();
            var blend = new BLENDFUNCTION { BlendOp = (byte)PInvoke.AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = (byte)PInvoke.AC_SRC_ALPHA };
            PInvoke.UpdateLayeredWindow(_wnd, HDC.Null, null, &size, mem, &src, new COLORREF(0), &blend, UPDATE_LAYERED_WINDOW_FLAGS.ULW_ALPHA);
        }
        finally
        {
            PInvoke.SelectObject(mem, old);
            PInvoke.DeleteObject(hbm);
            PInvoke.DeleteDC(mem);
            PInvoke.ReleaseDC(HWND.Null, screen);
        }
    }

    List<List<Row>> Groups()
    {
        string speedSample = _cfg.Bits ? "888.8 Mbps" : "888.8 MB/s";
        var net = new List<Row>();
        var sys = new List<Row>();
        if (_cfg.ShowUp) net.Add(new("↑", Calc.Speed(_stats.UpBps, _cfg.Bits), speedSample));
        if (_cfg.ShowDown) net.Add(new("↓", Calc.Speed(_stats.DownBps, _cfg.Bits), speedSample));
        if (_cfg.ShowCpu) sys.Add(new("CPU", $"{_stats.Cpu}%", "100%"));
        if (_cfg.ShowMemory) sys.Add(new("MEM", $"{_stats.Mem}%", "100%"));
        // temperatures are laid out like CPU/MEM: two rows per column, as many columns as needed
        var temps = new List<Row>();
        if (_cfg.ShowTemp && _stats.Temp > 0) temps.Add(new("TEMP", $"{_stats.Temp}°C", "100°C"));
        if (_stats.Admin is { Gpu: > 0 } a1) temps.Add(new("GPU", $"{a1.Gpu}°C", "100°C"));
        if (_stats.Admin is { Disk: > 0 } a2) temps.Add(new("DISK", $"{a2.Disk}°C", "100°C"));
        var groups = new List<List<Row>> { net, sys };
        for (int i = 0; i < temps.Count; i += 2) groups.Add(temps.GetRange(i, Math.Min(2, temps.Count - i)));
        if (_cfg.ShowBattery && _stats.Bat is { Present: true } b)
        {
            // label is the indicator: CHG charging, DIS discharging, AC plugged in and idle (full)
            var (label, value) = b.IsCharging ? ("CHG", $"+{Calc.Watts(b.Watts)}")
                : b.IsDischarging ? ("DIS", $"-{Calc.Watts(b.Watts)}")
                : b.OnAc ? ("AC", "Full") : ("BAT", "0 W");
            groups.Add([new(label, value, "-88.8 W")]);
        }
        return groups.Where(g => g.Count > 0).ToList();
    }

    Color TextColor()
    {
        if (_cfg.TextColor != "auto")
        {
            try { return ColorTranslator.FromHtml(_cfg.TextColor); }
            catch (Exception e) when (e is ArgumentException or FormatException) { /* fall through to auto */ }
        }
        bool light = Microsoft.Win32.Registry.GetValue(ThemeKey, "SystemUsesLightTheme", 0) is 1;
        return light ? Color.FromArgb(0x1B, 0x1B, 0x1B) : Color.White;
    }

    void ShowMenu()
    {
        var menu = PInvoke.CreatePopupMenu();
        var fontMenu = PInvoke.CreatePopupMenu();
        var offsetMenu = PInvoke.CreatePopupMenu();
        void Add(HMENU m, int id, string text, bool on = false) =>
            PInvoke.AppendMenu(m, MENU_ITEM_FLAGS.MF_STRING | (on ? MENU_ITEM_FLAGS.MF_CHECKED : 0), (nuint)id, text);

        Add(menu, IdUp, "Upload", _cfg.ShowUp);
        Add(menu, IdDown, "Download", _cfg.ShowDown);
        Add(menu, IdCpu, "CPU", _cfg.ShowCpu);
        Add(menu, IdMem, "Memory", _cfg.ShowMemory);
        Add(menu, IdTemp, "Temperature", _cfg.ShowTemp);
        Add(menu, IdBat, "Battery power", _cfg.ShowBattery);
        PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, null);
        Add(menu, IdBits, "Show speed in bits", _cfg.Bits);
        for (int i = 0; i < FontSizes.Length; i++) Add(fontMenu, IdFont + i, $"{FontSizes[i]} px", (int)_cfg.FontPx == FontSizes[i]);
        PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_POPUP, (nuint)fontMenu.Value, "Font size");
        Add(offsetMenu, IdLeft, "Move left 4 px");
        Add(offsetMenu, IdRight, "Move right 4 px");
        Add(offsetMenu, IdReset, "Reset position");
        PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_POPUP, (nuint)offsetMenu.Value, "Position");
        PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, null);
        var rateMenu = PInvoke.CreatePopupMenu();
        for (int i = 0; i < RatesMs.Length; i++) Add(rateMenu, IdRate + i, $"{RatesMs[i] / 1000.0:0.#} s", _cfg.RefreshMs == RatesMs[i]);
        PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_POPUP, (nuint)rateMenu.Value, "Refresh rate");
        Add(menu, IdAdmin, TaskbarMonitor.Admin.IsElevated ? "GPU/disk temperatures (running as admin)" : "GPU/disk temperatures (restart as admin)", _cfg.AdminSensors);
        Add(menu, IdAuto, "Start with Windows", Autostart.IsOn);
        Add(menu, IdExit, "Exit");

        PInvoke.GetCursorPos(out var pt);
        PInvoke.SetForegroundWindow(_owner);             // lets the menu close when clicking elsewhere
        var cmd = PInvoke.TrackPopupMenuEx(menu, TpmRightButton | TpmBottomAlign | TpmReturnCmd, pt.X, pt.Y, _owner, null);
        PInvoke.DestroyMenu(menu);                       // also destroys the submenus
        Apply(cmd.Value);
    }

    void Apply(int cmd)
    {
        int shown = (_cfg.ShowUp ? 1 : 0) + (_cfg.ShowDown ? 1 : 0) + (_cfg.ShowCpu ? 1 : 0) + (_cfg.ShowMemory ? 1 : 0) + (_cfg.ShowTemp ? 1 : 0);
        bool Toggle(bool on) => on && shown == 1 ? on : !on;     // never hide the last item

        switch (cmd)
        {
            case IdUp: _cfg.ShowUp = Toggle(_cfg.ShowUp); break;
            case IdDown: _cfg.ShowDown = Toggle(_cfg.ShowDown); break;
            case IdCpu: _cfg.ShowCpu = Toggle(_cfg.ShowCpu); break;
            case IdMem: _cfg.ShowMemory = Toggle(_cfg.ShowMemory); break;
            case IdTemp: _cfg.ShowTemp = Toggle(_cfg.ShowTemp); break;
            case IdBat: _cfg.ShowBattery = !_cfg.ShowBattery; break;
            case IdBits: _cfg.Bits = !_cfg.Bits; break;
            case >= IdFont and < IdFont + 6: _cfg.FontPx = FontSizes[cmd - IdFont]; break;
            case >= IdRate and < IdRate + 4: _cfg.RefreshMs = RatesMs[cmd - IdRate]; StartTimer(); break;
            case IdLeft: _cfg.OffsetX -= 4; break;
            case IdRight: _cfg.OffsetX += 4; break;
            case IdReset: _cfg.OffsetX = 0; break;
            case IdAdmin:
                _cfg.AdminSensors = !_cfg.AdminSensors;
                if (!_cfg.AdminSensors) { StopAdminSensors(); break; }
                if (TaskbarMonitor.Admin.IsElevated) { StartAdminSensors(); break; }
                _cfg.Save();
                if (TaskbarMonitor.Admin.RelaunchElevated()) { PInvoke.PostQuitMessage(0); return; }   // new elevated copy takes over
                _cfg.AdminSensors = false;                                                             // UAC declined
                break;
            case IdAuto: Autostart.Set(!Autostart.IsOn); return;
            case IdExit: PInvoke.PostQuitMessage(0); return;
            default: return;
        }
        _cfg.Save();
        Refresh();
    }
}
