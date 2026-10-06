using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace WukongBench;

internal sealed class Desktop(Process process)
{
    public bool Ready
    {
        get
        {
            if (process.HasExited) throw new InvalidOperationException("Benchmark process exited unexpectedly.");
            process.Refresh();
            return process.MainWindowHandle != IntPtr.Zero;
        }
    }
    public IntPtr Window
    {
        get
        {
            if (process.HasExited) throw new InvalidOperationException("Benchmark process exited unexpectedly.");
            process.Refresh();
            if (process.MainWindowHandle == IntPtr.Zero) throw new InvalidOperationException("Benchmark window is unavailable.");
            return process.MainWindowHandle;
        }
    }
    private Rectangle Client()
    {
        var window = Window;
        if (!GetClientRect(window, out var r)) throw new IOException("GetClientRect failed.");
        var point = new Point();
        if (!ClientToScreen(window, ref point)) throw new IOException("ClientToScreen failed.");
        return new Rectangle(point.X, point.Y, r.Right, r.Bottom);
    }
    public void Focus()
    {
        var w = Window;
        if (IsIconic(w)) ShowWindow(w, 9);
        SetForegroundWindow(w);
        if (GetForegroundWindow() != w) throw new InvalidOperationException("Cannot focus Benchmark Tool. Leave the desktop unlocked and do not switch windows during the run.");
    }
    public (int Width, int Height) Capture(string path)
    {
        Focus(); var r = Client();
        if (r.Width < 640 || r.Height < 360 || !SystemInformation.VirtualScreen.Contains(r))
            throw new InvalidOperationException("The benchmark window must be fully visible on a monitor (at least 640x360).");
        using var bitmap = new Bitmap(r.Width, r.Height);
        using (var g = Graphics.FromImage(bitmap)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
        var scale = Math.Min(1d, 2560d / Math.Max(r.Width, r.Height));
        using var resized = new Bitmap(bitmap, new Size((int)(r.Width * scale), (int)(r.Height * scale)));
        resized.Save(path, ImageFormat.Png);
        return (resized.Width, resized.Height);
    }
    public void Click(double x, double y, int imageWidth, int imageHeight)
    {
        Focus(); var r = Client();
        int px = r.X + (int)(x * r.Width / imageWidth), py = r.Y + (int)(y * r.Height / imageHeight);
        if (!r.Contains(px, py)) throw new ArgumentOutOfRangeException(nameof(x));
        SetCursorPos(px, py);
        Send([new Input { Type = 0, Data = new InputUnion { Mouse = new MouseInput { Flags = 2 } } },
              new Input { Type = 0, Data = new InputUnion { Mouse = new MouseInput { Flags = 4 } } }]);
    }
    public void Key(ushort code)
    {
        uint extended = code is >= 0x21 and <= 0x28 ? 1u : 0u;
        Focus(); Send([new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = code, Flags = extended } } },
                       new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = code, Flags = extended | 2 } } }]);
    }
    public void Scroll(int delta)
    {
        Focus(); var r = Client(); SetCursorPos(r.X + r.Width * 2 / 3, r.Y + r.Height / 2);
        Send([new Input { Type = 0, Data = new InputUnion { Mouse = new MouseInput { Flags = 0x800, MouseData = unchecked((uint)delta) } } }]);
    }
    private static void Send(Input[] input)
    {
        if (SendInput((uint)input.Length, input, Marshal.SizeOf<Input>()) != input.Length)
            throw new IOException("SendInput failed; run Steam and the runner at the same privilege level.");
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput
    { public int X, Y; public uint MouseData, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput
    { public ushort VirtualKey, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
}
