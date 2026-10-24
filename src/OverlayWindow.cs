using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using ImageLockMode = System.Drawing.Imaging.ImageLockMode;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace MotionCues
{
    /// <summary>
    /// Full-screen, transparent, click-through, always-on-top window that draws the motion-cue dots.
    /// Built in code (no XAML) to keep the project small.
    ///
    /// Motion model: a damped spring chases the target offset (gain * acceleration), and each dot is
    /// drawn at base + offset, wrapped inside its column band, so large offsets read as continuous
    /// scrolling rather than dots jumping back. Dots fade out near the wrap edges to hide the seam.
    ///
    /// Look: the window is excluded from screen capture, so the screen under each column is grabbed
    /// (inverted during the blit) many times a second, and each dot shows its slice of that image:
    /// whatever is behind a dot shows through it with inverted colours.
    /// </summary>
    public sealed class OverlayWindow : Window
    {
        public event Action? HotkeyPressed;

        private const int HotkeyId = 0x4D43; // "MC"
        private readonly Canvas _canvas = new Canvas { IsHitTestVisible = false };
        private readonly TextBlock _debugText;
        private readonly Border _debugBorder;
        private Ellipse[] _dots = Array.Empty<Ellipse>();
        private ImageBrush[] _brushes = Array.Empty<ImageBrush>();
        private IntPtr _hwnd;
        private HwndSource? _source;
        private readonly DispatcherTimer _topmostTimer;

        // screen capture, one inverted strip per column
        private const double CaptureInterval = 1.0 / 30;
        private bool _canSample;
        private double _captureClock;
        private double _dpiScale = 1;
        private System.Drawing.Point _screenOrigin;
        private readonly System.Drawing.Bitmap?[] _strips = new System.Drawing.Bitmap?[2];
        private readonly WriteableBitmap?[] _inverted = new WriteableBitmap?[2];
        private readonly double[] _stripLeft = new double[2]; // DIP

        // spring state (DIP)
        private double _px, _py, _vx, _vy;
        // dot layer opacity (fades in/out)
        private double _layerOpacity;
        public double TargetX { get; set; }
        public double TargetY { get; set; }
        public bool DotsVisible { get; set; } = true;
        /// <summary>True when dots show the inverted screen behind them (false = fixed grey fallback).</summary>
        public bool ColorSampling => _canSample;

        private Settings _settings;

        public OverlayWindow(Settings settings)
        {
            _settings = settings;

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            Focusable = false;
            IsHitTestVisible = false;
            Title = "Motion Cues Overlay";
            WindowStartupLocation = WindowStartupLocation.Manual;

            _debugText = new TextBlock
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Foreground = Brushes.White,
            };
            _debugBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 6, 8, 6),
                Child = _debugText,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
            };
            Canvas.SetZIndex(_debugBorder, 10);

            var root = new Grid { IsHitTestVisible = false };
            root.Children.Add(_canvas);
            root.Children.Add(_debugBorder);
            _debugBorder.HorizontalAlignment = HorizontalAlignment.Right;
            _debugBorder.VerticalAlignment = VerticalAlignment.Top;
            _debugBorder.Margin = new Thickness(0, 12, 12, 0);
            Content = root;

            SourceInitialized += OnSourceInitialized;
            SizeChanged += (_, _) => RebuildDots();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

            // Other topmost windows (task manager, games, the taskbar) can push us down; re-assert.
            _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _topmostTimer.Tick += (_, _) => AssertTopmost();
            _topmostTimer.Start();
        }

        public void ApplySettings(Settings s)
        {
            _settings = s;
            RebuildDots();
        }

        // ------------------------------------------------------------------ window plumbing

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            _hwnd = new WindowInteropHelper(this).Handle;

            // Click-through + no activation + hidden from Alt-Tab.
            long ex = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
            ex |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED |
                  NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
            ex &= ~(long)NativeMethods.WS_EX_APPWINDOW;
            NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, ex);

            // Without capture exclusion the samples would include our own dots and feed back on themselves.
            _canSample = NativeMethods.SetWindowDisplayAffinity(_hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE);

            _source = HwndSource.FromHwnd(_hwnd);
            _source?.AddHook(WndProc);

            if (!NativeMethods.RegisterHotKey(_hwnd, HotkeyId,
                    NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, 0x4D /* 'M' */))
            {
                System.Diagnostics.Debug.WriteLine("RegisterHotKey(Ctrl+Alt+M) failed - already in use?");
            }

            CoverPrimaryMonitor();
        }

        /// <summary>
        /// Position in physical pixels via SetWindowPos. With PerMonitorV2, WinForms' Screen bounds are
        /// physical pixels, and WPF converts the resulting size back to DIPs for layout, so this is
        /// correct at any scale factor.
        /// </summary>
        private void CoverPrimaryMonitor()
        {
            if (_hwnd == IntPtr.Zero) return;
            var b = Forms.Screen.PrimaryScreen?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
            _screenOrigin = b.Location;
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, b.X, b.Y, b.Width, b.Height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }

        private void AssertTopmost()
        {
            if (_hwnd == IntPtr.Zero) return;
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE |
                NativeMethods.SWP_NOOWNERZORDER);
        }

        private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
            Dispatcher.BeginInvoke(new Action(() => { CoverPrimaryMonitor(); RebuildDots(); }));

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
            {
                HotkeyPressed?.Invoke();
                handled = true;
            }
            return IntPtr.Zero;
        }

        protected override void OnClosed(EventArgs e)
        {
            _topmostTimer.Stop();
            foreach (var bmp in _strips) bmp?.Dispose();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            if (_hwnd != IntPtr.Zero) NativeMethods.UnregisterHotKey(_hwnd, HotkeyId);
            _source?.RemoveHook(WndProc);
            base.OnClosed(e);
        }

        // ------------------------------------------------------------------ dots

        private void RebuildDots()
        {
            _canvas.Children.Clear();
            int n = _settings.DotsPerSide;
            double d = _settings.DotDiameter;

            // Fallback when the screen can't be captured: dark fill + light outline, visible on most content.
            var fill = new SolidColorBrush(Color.FromArgb(200, 40, 40, 40)); fill.Freeze();
            var stroke = new SolidColorBrush(Color.FromArgb(220, 245, 245, 245)); stroke.Freeze();
            // Crisp 1:1 pixels instead of a blurred resample when dots sit between device pixels.
            RenderOptions.SetBitmapScalingMode(_canvas, BitmapScalingMode.NearestNeighbor);

            _dots = new Ellipse[n * 2];
            _brushes = new ImageBrush[n * 2];
            for (int i = 0; i < _dots.Length; i++)
            {
                _brushes[i] = new ImageBrush(_inverted[i / n])
                {
                    ViewboxUnits = BrushMappingMode.Absolute,
                    Stretch = Stretch.Fill,
                };
                var el = new Ellipse
                {
                    Width = d,
                    Height = d,
                    Fill = _canSample ? _brushes[i] : fill,
                    Stroke = _canSample ? null : stroke,
                    StrokeThickness = Math.Max(1.0, d / 9.0),
                    IsHitTestVisible = false,
                    Effect = null,
                };
                _dots[i] = el;
                _canvas.Children.Add(el);
            }
            _captureClock = CaptureInterval; // capture on the next frame
            UpdateDotPositions();
        }

        /// <summary>Snap the horizontal spring back to zero (used when switching flow mode).</summary>
        public void ResetX() { _px = 0; _vx = 0; }

        /// <summary>Advance the spring and redraw. dt in seconds.</summary>
        public void Step(double dt)
        {
            if (dt <= 0) return;
            dt = Math.Min(dt, 0.05); // avoid a big jump after a stall

            // Semi-implicit Euler spring, sub-stepped for stability at high stiffness.
            double k = _settings.SpringStiffness, c = 2 * _settings.SpringDampingRatio * Math.Sqrt(k);
            int sub = 4;
            double h = dt / sub;
            for (int i = 0; i < sub; i++)
            {
                _vx += (k * (TargetX - _px) - c * _vx) * h;
                _vy += (k * (TargetY - _py) - c * _vy) * h;
                _px += _vx * h;
                _py += _vy * h;
            }

            // Fade the whole layer in/out (~250 ms) on show/hide.
            double want = DotsVisible ? 1 : 0;
            double fade = 1 - Math.Exp(-dt / 0.08);
            _layerOpacity += (want - _layerOpacity) * fade;
            if (Math.Abs(_layerOpacity - want) < 0.003) _layerOpacity = want;
            _canvas.Opacity = _layerOpacity * _settings.DotOpacity;
            _canvas.Visibility = _layerOpacity <= 0 ? Visibility.Hidden : Visibility.Visible;

            if (_layerOpacity > 0)
            {
                if (_canSample) CaptureIfDue(dt);
                UpdateDotPositions();
            }
        }

        /// <summary>True when the dot layer has fully faded out (render loop can idle).</summary>
        public bool IsIdle => !DotsVisible && _layerOpacity <= 0;

        private void UpdateDotPositions()
        {
            double W = ActualWidth, H = ActualHeight;
            if (W <= 0 || H <= 0 || _dots.Length == 0) return;

            int n = _settings.DotsPerSide;
            double d = _settings.DotDiameter;
            double band = Math.Min(_settings.BandWidth, W / 3);
            double spacing = H / n;
            double fadeEdge = Math.Max(d * 1.5, 1);

            for (int side = 0; side < 2; side++)
            {
                // Column centre: EdgeMargin from the left or right edge.
                double cx = side == 0 ? _settings.EdgeMargin : W - _settings.EdgeMargin;
                double bandLeft = cx - band / 2;

                for (int i = 0; i < n; i++)
                {
                    // Alternate a small horizontal stagger so the columns read as a pattern, not a line.
                    double stagger = (i % 2 == 0 ? -1 : 1) * band * 0.15;
                    double baseX = cx + stagger;
                    double baseY = (i + 0.5) * spacing;

                    double x = bandLeft + Mod(baseX + _px - bandLeft, band);
                    double y = Mod(baseY + _py, H);

                    // Fade near the wrap seams so wrapping never "pops".
                    double ax = Math.Min(x - bandLeft, bandLeft + band - x) / fadeEdge;
                    double ay = Math.Min(y, H - y) / fadeEdge;
                    double a = Math.Clamp(Math.Min(ax, ay), 0, 1);

                    var el = _dots[side * n + i];
                    // Show the part of the inverted strip that lies under the dot.
                    _brushes[side * n + i].Viewbox = new Rect(x - d / 2 - _stripLeft[side], y - d / 2, d, d);
                    Canvas.SetLeft(el, x - d / 2);
                    Canvas.SetTop(el, y - d / 2);
                    el.Opacity = a;
                }
            }
        }

        private static double Mod(double a, double m) => ((a % m) + m) % m;

        // ------------------------------------------------------------------ screen capture

        private void CaptureIfDue(double dt)
        {
            _captureClock += dt;
            if (_captureClock < CaptureInterval) return;
            _captureClock = 0;
            try { CaptureStrips(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Screen capture failed: " + ex.Message); }
        }

        /// <summary>
        /// Grab each column's band from the screen (physical pixels), inverted by the blit itself
        /// (NOTSRCCOPY), and copy it into the bitmap the dots' brushes read from.
        /// </summary>
        private void CaptureStrips()
        {
            double W = ActualWidth, H = ActualHeight;
            if (W <= 0 || H <= 0 || _dots.Length == 0) return;
            _dpiScale = PresentationSource.FromVisual(this)?.CompositionTarget.TransformToDevice.M11 ?? 1;

            int n = _settings.DotsPerSide;
            double d = _settings.DotDiameter;
            double band = Math.Min(_settings.BandWidth, W / 3);
            // The band plus half a dot either side, so a dot at the band edge is still covered.
            int stripW = (int)Math.Ceiling((band + d) * _dpiScale) + 2;
            int stripH = (int)Math.Ceiling(H * _dpiScale);

            for (int side = 0; side < 2; side++)
            {
                double cx = side == 0 ? _settings.EdgeMargin : W - _settings.EdgeMargin;
                int left = (int)Math.Floor((cx - band / 2 - d / 2) * _dpiScale) - 1;
                _stripLeft[side] = left / _dpiScale;

                var bmp = _strips[side];
                if (bmp == null || bmp.Width != stripW || bmp.Height != stripH)
                {
                    bmp?.Dispose();
                    bmp = _strips[side] = new System.Drawing.Bitmap(stripW, stripH, PixelFormat.Format32bppRgb);
                }
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                    g.CopyFromScreen(_screenOrigin.X + left, _screenOrigin.Y, 0, 0, bmp.Size,
                        System.Drawing.CopyPixelOperation.NotSourceCopy);

                var wb = _inverted[side];
                if (wb == null || wb.PixelWidth != stripW || wb.PixelHeight != stripH)
                {
                    // DPI matches the screen so one bitmap pixel is one device pixel and Viewbox is in DIPs.
                    wb = _inverted[side] = new WriteableBitmap(stripW, stripH, 96 * _dpiScale, 96 * _dpiScale,
                        PixelFormats.Bgr32, null);
                    for (int i = 0; i < n; i++) _brushes[side * n + i].ImageSource = wb;
                }

                var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, stripW, stripH),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                try { wb.WritePixels(new Int32Rect(0, 0, stripW, stripH), data.Scan0, data.Stride * stripH, data.Stride); }
                finally { bmp.UnlockBits(data); }
            }
        }

        // ------------------------------------------------------------------ debug readout

        public void SetDebugVisible(bool on) =>
            _debugBorder.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        public void SetDebugText(string s) => _debugText.Text = s;
    }
}
