using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace EndfieldPriceOverlay.Services;

public sealed record CapturedFrame(BitmapSource Image, nint WindowHandle, string WindowTitle);

public sealed class WindowCaptureService
{
    private const uint Srccopy = 0x00CC0020;
    private const uint Captureblt = 0x40000000;

    public CapturedFrame Capture(string titleContains = "Endfield")
    {
        var target = FindWindow(titleContains);
        var width = target.Bounds.Right - target.Bounds.Left;
        var height = target.Bounds.Bottom - target.Bounds.Top;
        var screenDc = GetDC(0);
        if (screenDc == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取屏幕。");
        }

        var memoryDc = CreateCompatibleDC(screenDc);
        var bitmap = CreateCompatibleBitmap(screenDc, width, height);
        var previous = SelectObject(memoryDc, bitmap);
        try
        {
            if (!BitBlt(
                    memoryDc,
                    0,
                    0,
                    width,
                    height,
                    screenDc,
                    target.Bounds.Left,
                    target.Bounds.Top,
                    Srccopy | Captureblt))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法截取 Endfield 窗口。");
            }

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap,
                0,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return new CapturedFrame(source, target.Handle, target.Title);
        }
        finally
        {
            SelectObject(memoryDc, previous);
            DeleteObject(bitmap);
            DeleteDC(memoryDc);
            ReleaseDC(0, screenDc);
        }
    }

    public WindowTarget FindWindow(string titleContains = "Endfield")
    {
        var matches = new List<WindowTarget>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle) || IsIconic(handle))
            {
                return true;
            }

            var length = GetWindowTextLength(handle);
            if (length <= 0)
            {
                return true;
            }

            var title = new StringBuilder(length + 1);
            GetWindowText(handle, title, title.Capacity);
            if (!title.ToString().Contains(titleContains, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!GetClientRect(handle, out var client))
            {
                return true;
            }

            var topLeft = new NativePoint(client.Left, client.Top);
            var bottomRight = new NativePoint(client.Right, client.Bottom);
            if (!ClientToScreen(handle, ref topLeft) || !ClientToScreen(handle, ref bottomRight))
            {
                return true;
            }

            var bounds = new NativeRect(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
            if (bounds.Area > 0)
            {
                matches.Add(new WindowTarget(handle, title.ToString(), bounds));
            }

            return true;
        }, 0);

        return matches.MaxBy(match => match.Bounds.Area)
            ?? throw new InvalidOperationException($"未找到标题包含“{titleContains}”的可见窗口。");
    }

    public sealed record WindowTarget(nint Handle, string Title, NativeRect Bounds);

    [StructLayout(LayoutKind.Sequential)]
    public struct NativePoint(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeRect(int left, int top, int right, int bottom)
    {
        public int Left = left;
        public int Top = top;
        public int Right = right;
        public int Bottom = bottom;

        public long Area => Math.Max(0, Right - Left) * (long)Math.Max(0, Bottom - Top);
    }

    private delegate bool EnumWindowsProc(nint handle, nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint handle);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    private static extern int GetWindowTextLength(nint handle);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint handle, StringBuilder text, int maximumCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint handle, out NativeRect rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint handle, ref NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetDC(nint window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateCompatibleDC(nint deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateCompatibleBitmap(nint deviceContext, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint deviceContext, nint value);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint value);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(
        nint destination,
        int destinationX,
        int destinationY,
        int width,
        int height,
        nint source,
        int sourceX,
        int sourceY,
        uint rasterOperation);
}
