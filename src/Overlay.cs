using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace MotionCues
{
    /// <summary>
    /// Draws the motion-cue dots in two transparent, click-through, always-on-top strip windows, one per
    /// dot column (see <see cref="LayerWindow"/>), plus a small window for the debug readout.
    ///
    /// Motion model: a damped spring chases the target offset (gain * acceleration), and each dot is
    /// drawn at base + offset, wrapped inside its column band, so large offsets read as continuous
    /// scrolling rather than dots jumping back. Dots fade out near the wrap edges to hide the seam.
    ///
    /// Look: the windows are excluded from screen capture, so the screen under each strip is grabbed
    /// (inverted during the blit) on a background thread (<see cref="CaptureWorker"/>), and each dot
    /// shows its slice of that image: whatever is behind a dot shows through it with inverted colours.
    /// Captures run often while anything moves and slowly while everything is still.
    ///
    /// Steady motion also starts a <see cref="ParticleFountain"/> per side.
    /// All positions are in screen DIPs; each strip window's canvas is offset by its left edge.
    /// </summary>
    public sealed class Overlay
    {
        public event Action? HotkeyPressed;

        private const int HotkeyId = 0x4D43; // "MC"
        private readonly LayerWindow[] _sides = new LayerWindow[2];
        private readonly LayerWindow _debugWindow;
        private System.Drawing.Rectangle _debugRect;
        private readonly TextBlock _debugText;
        private readonly HwndSource? _hotkeySource;
        private readonly DispatcherTimer _topmostTimer;
        private Ellipse[] _dots = Array.Empty<Ellipse>();
        private ImageBrush[] _brushes = Array.Empty<ImageBrush>();
        private readonly ParticleFountain?[] _fountains = new ParticleFountain?[2];

        // geometry: primary monitor in physical pixels, and each strip window's rectangle within it
        private System.Drawing.Rectangle _screen;
        private double _scale = 1; // physical pixels per DIP
        private readonly System.Drawing.Rectangle[] _strip = new System.Drawing.Rectangle[2];  // within the screen
        private readonly System.Drawing.Rectangle[] _stripOnScreen = new System.Drawing.Rectangle[2];
        private readonly double[] _stripLeft = new double[2]; // DIP
        private double W => _screen.Width / _scale;
        private double H => _screen.Height / _scale;

        // screen capture, one inverted strip per column; ms between captures while moving / while still
        // Each grab makes the compositor do an extra pass, which costs the dots frames: measured in demo
        // mode, grabbing every 33 / 66 / 100 ms left 36 / 50 / 55 fps. Moving dots slide over the last grab
        // smoothly, so only video behind a dot shows the slower rate.
        private const int CaptureMovingMs = 66, CaptureStillMs = 100;
        private readonly bool _canSample;
        private readonly CaptureWorker _capture = new();

        // spring state (DIP)
        private double _px, _py, _vx, _vy;
        // dot layer opacity (fades in/out)
        private double _layerOpacity;
        public double TargetX { get; set; }
        public double TargetY { get; set; }
        public bool DotsVisible { get; set; } = true;
        /// <summary>True when dots show the inverted screen behind them (false = fixed grey fallback).</summary>
        public bool ColorSampling => _canSample;
        /// <summary>Time the last capture of both strips took, for the debug readout.</summary>
        public double CaptureMs => _capture.LastMs;

        // fountain: motion in the dots' direction (g, +x right, +y down), smoothed so only steady motion counts
        private const double FountainFullG = 0.15; // above the start level, the rate where the fountain is at full strength
        private double _steadyX, _steadyY, _dirX = 1, _dirY;
        public double MotionX { get; set; }
        public double MotionY { get; set; }
        public double FountainIntensity { get; private set; }
        public int ParticleCount => (_fountains[0]?.Count ?? 0) + (_fountains[1]?.Count ?? 0);

        private Settings _settings;

        public Overlay(Settings settings)
        {
            _settings = settings;

            _sides[0] = new LayerWindow("Motion Cues Overlay (left)");
            _sides[1] = new LayerWindow("Motion Cues Overlay (right)");
            _canSample = _sides[0].ExcludedFromCapture && _sides[1].ExcludedFromCapture;

            _debugText = new TextBlock
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Foreground = Brushes.White,
            };
            var debugBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 6, 8, 6),
                Child = _debugText,
                IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
            };
            _debugWindow = new LayerWindow("Motion Cues Debug", debugBorder);

            _hotkeySource = HwndSource.FromHwnd(_sides[0].Handle);
            _hotkeySource?.AddHook(WndProc);
            if (!NativeMethods.RegisterHotKey(_sides[0].Handle, HotkeyId,
                    NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, 0x4D /* 'M' */))
            {
                System.Diagnostics.Debug.WriteLine("RegisterHotKey(Ctrl+Alt+M) failed - already in use?");
            }

            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

            _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _topmostTimer.Tick += (_, _) =>
            {
                foreach (var w in _sides) w.AssertTopmost();
                if (_debugWindow.IsVisible) _debugWindow.AssertTopmost();
            };
            _topmostTimer.Start();
        }

        public void Show()
        {
            foreach (var w in _sides) w.Show();
            UpdateGeometry();
        }

        public void Close()
        {
            _topmostTimer.Stop();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            NativeMethods.UnregisterHotKey(_sides[0].Handle, HotkeyId);
            _hotkeySource?.RemoveHook(WndProc);
            _capture.Dispose();
            foreach (var w in _sides) w.Close();
            _debugWindow.Close();
        }

        public void ApplySettings(Settings s)
        {
            _settings = s;
            UpdateGeometry();
            RebuildDots();
        }

        /// <summary>A setting changed that doesn't need new dots, but may move the strip edges.</summary>
        public void SettingsChanged() => UpdateGeometry();

        // ------------------------------------------------------------------ window plumbing

        /// <summary>Size each strip window to the band (plus half a dot) or the fountain area, whichever is wider.</summary>
        private void UpdateGeometry()
        {
            _screen = Forms.Screen.PrimaryScreen?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
            var hmon = NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = _screen.X, Y = _screen.Y },
                NativeMethods.MONITOR_DEFAULTTOPRIMARY);
            _scale = NativeMethods.GetDpiForMonitor(hmon, NativeMethods.MDT_EFFECTIVE_DPI, out uint dpi, out _) == 0 ? dpi / 96.0 : 1;

            double d = _settings.DotDiameter;
            double band = Math.Min(_settings.BandWidth, W / 3);
            for (int side = 0; side < 2; side++)
            {
                double cx = ColumnCentre(side);
                double lo = cx - band / 2 - d / 2, hi = cx + band / 2 + d / 2;
                if (_settings.FountainEnabled)
                {
                    var r = FountainRegion(side);
                    double pad = Math.Max(_settings.ParticleSizeMin, _settings.ParticleSizeMax) / 2;
                    lo = Math.Min(lo, r.Left - pad);
                    hi = Math.Max(hi, r.Right + pad);
                }
                int left = Math.Max(0, (int)Math.Floor(lo * _scale) - 1);
                int right = Math.Min(_screen.Width, (int)Math.Ceiling(hi * _scale) + 1);
                var rect = new System.Drawing.Rectangle(left, 0, Math.Max(1, right - left), _screen.Height);
                if (rect == _strip[side]) continue;
                _strip[side] = rect;
                _stripLeft[side] = left / _scale;
                rect.Offset(_screen.Location);
                _stripOnScreen[side] = rect;
                _sides[side].Place(rect);
            }

            const int debugW = 620, debugH = 200; // DIP; the readout hugs the top-right corner of this box
            int dw = (int)(debugW * _scale), dh = (int)(debugH * _scale), margin = (int)(12 * _scale);
            _debugRect = new System.Drawing.Rectangle(_screen.Right - dw - margin, _screen.Y + margin, dw, dh);
            if (_debugWindow.IsVisible) _debugWindow.Place(_debugRect);
        }

        private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
            _sides[0].Dispatcher.BeginInvoke(new Action(() => { UpdateGeometry(); RebuildDots(); }));

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
            {
                HotkeyPressed?.Invoke();
                handled = true;
            }
            return IntPtr.Zero;
        }

        // ------------------------------------------------------------------ dots

        private void RebuildDots()
        {
            foreach (var w in _sides) w.Surface.Children.Clear();
            int n = _settings.DotsPerSide;
            double d = _settings.DotDiameter;

            // Fallback when the screen can't be captured: dark fill + light outline, visible on most content.
            var fill = new SolidColorBrush(Color.FromArgb(200, 40, 40, 40)); fill.Freeze();
            var stroke = new SolidColorBrush(Color.FromArgb(220, 245, 245, 245)); stroke.Freeze();

            _dots = new Ellipse[n * 2];
            _brushes = new ImageBrush[n * 2];
            for (int i = 0; i < _dots.Length; i++)
            {
                _brushes[i] = new ImageBrush(_capture.Image(i / n))
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
                _sides[i / n].Surface.Children.Add(el);
            }
            for (int side = 0; side < 2; side++)
            {
                var surface = _sides[side].Surface;
                // Crisp 1:1 pixels instead of a blurred resample when dots sit between device pixels.
                RenderOptions.SetBitmapScalingMode(surface, BitmapScalingMode.NearestNeighbor);
                _fountains[side] = new ParticleFountain(surface, _capture.Image(side), _canSample, fill, stroke, seed: side + 1);
            }
            UpdateDotPositions();
        }

        /// <summary>Snap the horizontal spring back to zero (used when switching flow mode).</summary>
        public void ResetX() { _px = 0; _vx = 0; }

        /// <summary>Advance the spring and redraw. dt in seconds.</summary>
        public void Step(double dt)
        {
            if (dt <= 0) return;
            dt = Math.Min(dt, 0.25); // slow ticks are up to 100 ms; anything far beyond is a stall

            // Semi-implicit Euler spring, sub-stepped at <= 4 ms so it stays stable at the stiffest,
            // most damped settings on slow ticks too.
            double k = _settings.SpringStiffness, c = 2 * _settings.SpringDampingRatio * Math.Sqrt(k);
            int sub = Math.Max(4, (int)Math.Ceiling(dt / 0.004));
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
            foreach (var w in _sides)
            {
                w.Surface.Opacity = _layerOpacity * _settings.DotOpacity;
                w.Surface.Visibility = _layerOpacity <= 0 ? Visibility.Hidden : Visibility.Visible;
            }

            // Only motion that holds for a while (~0.6 s) feeds the fountain; jolts barely register.
            double steady = 1 - Math.Exp(-dt / 0.6);
            _steadyX += (MotionX - _steadyX) * steady;
            _steadyY += (MotionY - _steadyY) * steady;
            double m = Math.Sqrt(_steadyX * _steadyX + _steadyY * _steadyY);
            if (m > 1e-4) { _dirX = _steadyX / m; _dirY = _steadyY / m; }
            bool emitting = DotsVisible && _settings.FountainEnabled && _settings.FountainAmount > 0;
            double wantFountain = emitting ? Math.Clamp((m - _settings.FountainStartG) / FountainFullG, 0, 1) : 0;
            FountainIntensity += (wantFountain - FountainIntensity) * (1 - Math.Exp(-dt / 0.3));

            if (_layerOpacity > 0)
            {
                UpdateDotPositions();
                StepFountains(dt);
            }
            else
            {
                // Hidden: drop the particles, or they would keep counting as motion.
                foreach (var f in _fountains) f?.Clear();
                FountainIntensity = 0;
            }
            if (_canSample) UpdateCapture();
        }

        /// <summary>True when the dot layer has fully faded out (render loop can idle).</summary>
        public bool IsIdle => !DotsVisible && _layerOpacity <= 0;

        /// <summary>How fast the fastest thing on screen moves, DIP per second (picks the frame rate).</summary>
        public double ScreenSpeed => _layerOpacity <= 0 ? 0 :
            Math.Max(Math.Sqrt(_vx * _vx + _vy * _vy), ParticleCount > 0 ? _settings.FountainSpeed * 1.5 : 0);

        /// <summary>Nothing on screen is moving or fading, so the app can drop to a slow tick.</summary>
        public bool IsSettled => _layerOpacity <= 0 ||
            // A creep of a few DIP per second (sensor noise) is invisible at 10 fps too.
            Math.Abs(_vx) < 3 && Math.Abs(_vy) < 3 &&
            Math.Abs(TargetX - _px) < 1 && Math.Abs(TargetY - _py) < 1 &&
            ParticleCount == 0 && FountainIntensity < 0.01 && _layerOpacity >= 1;

        private void UpdateDotPositions()
        {
            if (W <= 0 || H <= 0 || _dots.Length == 0) return;

            int n = _settings.DotsPerSide;
            double d = _settings.DotDiameter;
            double band = Math.Min(_settings.BandWidth, W / 3);
            double spacing = H / n;
            double fadeEdge = Math.Max(d * 1.5, 1);

            for (int side = 0; side < 2; side++)
            {
                double cx = ColumnCentre(side);
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

                    int idx = side * n + i;
                    var el = _dots[idx];
                    double left = x - d / 2 - _stripLeft[side];
                    // Show the part of the inverted strip that lies under the dot.
                    _brushes[idx].Viewbox = new Rect(left, y - d / 2, d, d);
                    Canvas.SetLeft(el, left);
                    Canvas.SetTop(el, y - d / 2);
                    el.Opacity = a;
                }
            }
        }

        private static double Mod(double a, double m) => ((a % m) + m) % m;

        /// <summary>Column centre: EdgeMargin from the left or right edge.</summary>
        private double ColumnCentre(int side) => side == 0 ? _settings.EdgeMargin : W - _settings.EdgeMargin;

        /// <summary>Where one side's fountain particles live (DIP): FountainWidth around the column, full height.</summary>
        private Rect FountainRegion(int side)
        {
            double cx = ColumnCentre(side), half = _settings.FountainWidth / 2;
            double left = Math.Max(0, cx - half), right = Math.Min(W, cx + half);
            return new Rect(left, 0, Math.Max(0, right - left), H);
        }

        private void StepFountains(double dt)
        {
            if (W <= 0 || H <= 0) return;
            for (int side = 0; side < 2; side++)
                _fountains[side]?.Step(dt, FountainRegion(side), _dirX, _dirY, FountainIntensity, _stripLeft[side], _settings);
        }

        // ------------------------------------------------------------------ screen capture

        /// <summary>Tell the capture thread what to grab and how often, and take in what it grabbed.</summary>
        private void UpdateCapture()
        {
            _capture.Configure(_stripOnScreen[0], _stripOnScreen[1], _scale,
                IsSettled ? CaptureStillMs : CaptureMovingMs, enabled: _layerOpacity > 0);

            int replaced = _capture.Apply();
            if (replaced == 0) return;
            int n = _settings.DotsPerSide;
            for (int side = 0; side < 2; side++)
            {
                if ((replaced & (1 << side)) == 0) continue;
                var image = _capture.Image(side)!;
                for (int i = 0; i < n && side * n + i < _brushes.Length; i++) _brushes[side * n + i].ImageSource = image;
                _fountains[side]?.SetImage(image);
            }
        }

        // ------------------------------------------------------------------ debug readout

        public void SetDebugVisible(bool on)
        {
            if (on && !_debugWindow.IsVisible) { _debugWindow.Show(); _debugWindow.Place(_debugRect); }
            else if (!on && _debugWindow.IsVisible) _debugWindow.Hide();
        }

        public void SetDebugText(string s) => _debugText.Text = s;
    }
}
