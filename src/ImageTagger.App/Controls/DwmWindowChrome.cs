using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace ImageTagger.App.Controls;

/// <summary>
/// 让无边框自定义窗口重新获得系统合成器的阴影与窗口动画。
/// ShadUI Window 使用扩展客户端区 + 透明背景，DWM 默认不再为这种窗口绘制阴影；
/// 这里显式启用非客户区渲染并扩展 1px 玻璃边框，让 DWM 把窗口当作有边框对待，
/// 从而正常合成系统阴影与打开/最小化/最大化动画。1px 玻璃边被内容完全覆盖，视觉无影响。
/// 仅 Windows 生效，其他平台保持默认行为；全程最佳努力，失败静默忽略。
/// </summary>
public static class DwmWindowChrome
{
    /// <summary>DWMWA_NCRENDERING_POLICY：强制非客户区渲染策略。</summary>
    private const int DWMWA_NCRENDERING_POLICY = 7;

    /// <summary>DWMNCRP_ENABLED：始终启用非客户区渲染。</summary>
    private const int DWMNCRP_ENABLED = 2;

    /// <summary>为已显示的窗口启用系统阴影与动画；HWND 不存在或失败时直接返回。</summary>
    public static void EnableSystemShadow(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            var handle = window.TryGetPlatformHandle();
            if (handle is null || handle.Handle == IntPtr.Zero)
                return;
            var hwnd = handle.Handle;
            int policy = DWMNCRP_ENABLED;
            if (DwmSetWindowAttribute(hwnd, DWMWA_NCRENDERING_POLICY, ref policy, sizeof(int)) != 0)
                return;
            var margins = new Margins(1, 1, 1, 1);
            DwmExtendFrameIntoClientArea(hwnd, ref margins);
        }
        catch (Exception)
        {
            // 纯视觉偏好：失败时保持无阴影现状，不影响窗口功能。
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins(int left, int right, int top, int bottom)
    {
        public int Left = left;
        public int Right = right;
        public int Top = top;
        public int Bottom = bottom;
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins pMarInset);
}
