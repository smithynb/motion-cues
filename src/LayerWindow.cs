using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace MotionCues
{
    /// <summary>
    /// Transparent, click-through, always-on-top window placed in physical pixels. The overlay uses one
    /// per dot column instead of one full-screen window, because a transparent WPF window is redrawn and
    /// copied as a whole every frame: two narrow strips cost a fraction of the full screen.
    /// Built in code (no XAML) to keep the project small.
    /// </summary>
    internal sealed class LayerWindow : Window
    {
        public readonly Canvas Surface = new Canvas { IsHitTestVisible = false };
        public IntPtr Handle { get; }
        /// <summary>False when Windows refused to hide the window from screen capture.</summary>
        public bool ExcludedFromCapture { get; }

        public LayerWindow(string title, UIElement? content = null)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            Focusable = false;
            IsHitTestVisible = false;
            Title = title;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Content = content ?? Surface;

            Handle = new WindowInteropHelper(this).EnsureHandle();

            // Click-through + no activation + hidden from Alt-Tab.
            long ex = NativeMethods.GetWindowLong(Handle, NativeMethods.GWL_EXSTYLE);
            ex |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED |
                  NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
            ex &= ~(long)NativeMethods.WS_EX_APPWINDOW;
            NativeMethods.SetWindowLong(Handle, NativeMethods.GWL_EXSTYLE, ex);

            // Without capture exclusion the screen capture would include our own dots and feed back on itself.
            // MOTIONCUES_CAPTURABLE=1 skips it (grey fallback dots) so the overlay shows up in screenshots.
            ExcludedFromCapture = Environment.GetEnvironmentVariable("MOTIONCUES_CAPTURABLE") != "1" &&
                NativeMethods.SetWindowDisplayAffinity(Handle, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
        }

        /// <summary>
        /// Move and resize in physical pixels via SetWindowPos. With PerMonitorV2, WPF converts the size
        /// back to DIPs for layout, so this is correct at any scale factor.
        /// </summary>
        public void Place(System.Drawing.Rectangle r) =>
            NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, r.X, r.Y, Math.Max(1, r.Width), Math.Max(1, r.Height),
                NativeMethods.SWP_NOACTIVATE);

        /// <summary>Other topmost windows (task manager, games, the taskbar) can push us down; re-assert.</summary>
        public void AssertTopmost() =>
            NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE |
                NativeMethods.SWP_NOOWNERZORDER);
    }
}
