using System;
using System.Windows;
using System.Windows.Interop;

namespace FloorPatternFlattener.UI
{
    /// <summary>
    /// Shared chrome for add-in windows. A fixed <see cref="Window.Height"/> clips the
    /// buttons once the caption and fonts grow under Per-Monitor DPI. Size-to-content
    /// is applied again after the HWND exists, which is when WPF learns the real scale.
    /// </summary>
    internal static class WpfDialog
    {
        public static void Prepare(Window window, SizeToContent sizeToContent)
        {
            window.WindowStyle = WindowStyle.SingleBorderWindow;
            window.ResizeMode = ResizeMode.CanResizeWithGrip;
            window.SizeToContent = sizeToContent;
            window.SnapsToDevicePixels = true;
            window.UseLayoutRounding = true;
            window.ShowInTaskbar = false;

            var fitted = false;
            window.ContentRendered += (_, __) =>
            {
                if (fitted) return;
                fitted = true;
                Refit(window, sizeToContent);
            };
            window.DpiChanged += (_, __) => Refit(window, sizeToContent);
        }

        public static void Own(Window window, IntPtr owner)
        {
            if (owner == IntPtr.Zero) return;
            try { new WindowInteropHelper(window).Owner = owner; }
            catch { /* Revit handle not available */ }
        }

        private static void Refit(Window window, SizeToContent sizeToContent)
        {
            if (window.WindowState != WindowState.Normal) return;
            window.SizeToContent = SizeToContent.Manual;
            window.UpdateLayout();
            window.SizeToContent = sizeToContent;
            ClampToWorkArea(window);
        }

        private static void ClampToWorkArea(Window window)
        {
            var area = SystemParameters.WorkArea;
            var maxH = Math.Max(160, area.Height * 0.9);
            var maxW = Math.Max(240, area.Width * 0.9);
            if (window.ActualHeight > maxH || window.ActualWidth > maxW)
            {
                window.SizeToContent = SizeToContent.Manual;
                if (window.ActualHeight > maxH) window.Height = maxH;
                if (window.ActualWidth > maxW) window.Width = maxW;
            }
        }
    }
}
