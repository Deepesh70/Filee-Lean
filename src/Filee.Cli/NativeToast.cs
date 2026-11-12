// Displays a native Windows Action Center notification when invoked from Explorer or CLI.
// Runs in-process with 0 external dependencies and zero resident memory.

using System.Runtime.InteropServices;

namespace Filee.Cli;

internal static class NativeToast
{
    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_INFO = 0x00000010;

    private const uint NIIF_INFO = 0x00000001;
    private const uint NIIF_WARNING = 0x00000002;
    private const uint NIIF_ERROR = 0x00000003;

    private const nint HWND_MESSAGE = -3;
    private const uint WS_POPUP = 0x80000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadIconW(nint hInstance, nint lpIconName);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hWnd);

    public static void Show(string title, string message, bool isError = false)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            // Create a lightweight message-only window to anchor the notify icon
            var hWnd = CreateWindowEx(0, "STATIC", "FileeNotification", WS_POPUP, 0, 0, 0, 0, HWND_MESSAGE, 0, 0, 0);
            if (hWnd == 0)
                return;

            try
            {
                const nint IDI_APPLICATION = 32512;
                var hIcon = LoadIconW(0, IDI_APPLICATION);

                var nid = new NOTIFYICONDATAW
                {
                    cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                    hWnd = hWnd,
                    uID = 101,
                    uFlags = NIF_INFO | NIF_ICON | NIF_TIP,
                    hIcon = hIcon,
                    szTip = "Filee",
                    szInfoTitle = title.Length > 63 ? title[..63] : title,
                    szInfo = message.Length > 255 ? message[..255] : message,
                    dwInfoFlags = isError ? NIIF_ERROR : NIIF_INFO,
                    uTimeoutOrVersion = 3000
                };

                Shell_NotifyIcon(NIM_ADD, ref nid);
                Shell_NotifyIcon(NIM_MODIFY, ref nid);
                Thread.Sleep(300);
                Shell_NotifyIcon(NIM_DELETE, ref nid);
            }
            finally
            {
                DestroyWindow(hWnd);
            }
        }
        catch
        {
            // Notification failure must never prevent conversion or fail the CLI
        }
    }
}
