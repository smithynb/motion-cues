using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace MotionCues
{
    /// <summary>
    /// Application entry point: wires sensor -> processor -> overlay, owns the tray icon,
    /// the render loop, settings persistence, auto mode and demo mode.
    /// </summary>
    public sealed class App : Application
    {
        [STAThread]
        public static int Main()
        {
            // Single instance: a second launch just exits.
            using var mutex = new Mutex(true, @"Local\MotionCues.SingleInstance", out bool created);
            if (!created) return 0;

            var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            return app.Run();
        }

        private Settings _settings = new Settings();
        private readonly MotionProcessor _proc = new MotionProcessor();
        private readonly AutoVisibility _auto = new AutoVisibility();
        private AccelerometerSource? _sensor;
        private DemoSource? _demo;
        private DemoWindow? _demoWindow;
        private SettingsWindow? _settingsWindow;
        private Overlay? _overlay;
        private ScreenDimmer? _dimmer;
        private Forms.NotifyIcon? _tray;
        private Icon? _trayIcon;
        private IntPtr _trayHIcon;

        // menu items whose check state we update
        private Forms.ToolStripMenuItem? _miShow, _miInvX, _miInvZ, _miDebug, _miAuto, _miDemo, _miFlow, _miDim;
        private Forms.ToolStripMenuItem? _miLow, _miMed, _miHigh;

        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _lastFrame;
        private double _lastDebug;
        // frame rate over the last second, for the debug readout and MOTIONCUES_PERFLOG
        private int _frames;
        private double _fpsStart, _fps;
        private readonly string? _perfLog = Environment.GetEnvironmentVariable("MOTIONCUES_PERFLOG");
        private bool _renderHooked;
        // Frame interval in ms. VsyncMs means every display frame (CompositionTarget.Rendering);
        // anything longer runs on a timer, so slow or still dots cost fewer frames.
        private const int VsyncMs = 16;
        private int _frameMs = VsyncMs;
        private readonly DispatcherTimer _slowTick = new();
        private bool _slowRunning;
        private double _slowerFor;
        private double _flowX;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += (_, ex) =>
            {
                Debug.WriteLine(ex.Exception);
                ex.Handled = true; // an overlay should never take the user's session down with it
            };

            _slowTick.Tick += (_, _) => Frame();

            _settings = Settings.Load();
            _settings.ApplyTo(_proc);
            AutoStart.RefreshPath();
            _auto.CalmSeconds = _settings.AutoCalmSeconds;

            _dimmer = new ScreenDimmer();
            _overlay = new Overlay(_settings);
            _overlay.HotkeyPressed += ToggleDots;
            _overlay.Show();
            _overlay.ApplySettings(_settings);
            _overlay.SetDebugVisible(_settings.ShowDebug);
            _dimmer.Apply(_settings.DimMode, _settings.DimAmount);

            CreateTray();

            _sensor = AccelerometerSource.TryCreate(_proc, _settings.ReportIntervalMs);
            if (_sensor == null)
            {
                _tray!.ShowBalloonTip(5000, "Motion Cues",
                    "No accelerometer was found. Use 'Demo mode' in the tray menu to test the overlay.",
                    Forms.ToolTipIcon.Warning);
                var r = MessageBox.Show(
                    "No accelerometer was found on this PC (Windows.Devices.Sensors.Accelerometer.GetDefault() returned null).\n\n" +
                    "Start demo mode so you can still test the overlay?",
                    "Motion Cues", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r == MessageBoxResult.Yes) SetDemo(true);
            }
            else
            {
                _tray!.Text = Truncate($"Motion Cues - sensor @ {_sensor.ActualReportInterval} ms");
            }

            UpdateMenuChecks();
            UpdateRenderHook();

            if (Array.Exists(e.Args, a => a.Equals("--settings", StringComparison.OrdinalIgnoreCase)))
                OpenSettings();
            if (Array.Exists(e.Args, a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase)))
                SetDemo(true);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            CompositionTarget.Rendering -= OnRendering;
            _slowTick.Stop();
            _overlay?.Close();
            _dimmer?.Dispose();
            _sensor?.Dispose();
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            _trayIcon?.Dispose();
            if (_trayHIcon != IntPtr.Zero) NativeMethods.DestroyIcon(_trayHIcon);
            _settings.Save();
            base.OnExit(e);
        }

        // ================================================================== render loop

        /// <summary>Is anything supposed to be on screen? (manual toggle, or auto mode decision)</summary>
        private bool WantDots => _settings.ShowDots && (!_settings.AutoMode || _auto.Active || _demo != null);

        /// <summary>
        /// Run the frame loop only while something can change, so the app costs ~nothing when dots are
        /// hidden. Fast motion runs on every display frame (CompositionTarget.Rendering); slower motion and
        /// still dots run on a timer at a lower rate (see <see cref="FrameMsFor"/>). In auto mode the loop
        /// stays on while auto mode is armed.
        /// </summary>
        private void UpdateRenderHook()
        {
            bool need = _settings.ShowDots || _settings.ShowDebug || _demo != null || !(_overlay?.IsIdle ?? true);
            bool fast = need && _frameMs <= VsyncMs, slow = need && _frameMs > VsyncMs;
            _slowTick.Interval = TimeSpan.FromMilliseconds(Math.Max(_frameMs, VsyncMs));
            if (!_renderHooked && !_slowRunning && (fast || slow))
                _lastFrame = _clock.Elapsed.TotalSeconds;
            if (fast != _renderHooked)
            {
                if (fast) CompositionTarget.Rendering += OnRendering;
                else CompositionTarget.Rendering -= OnRendering;
                _renderHooked = fast;
            }
            if (slow != _slowRunning)
            {
                if (slow) _slowTick.Start(); else _slowTick.Stop();
                _slowRunning = slow;
            }
        }

        private void OnRendering(object? sender, EventArgs e) => Frame();

        private void Frame()
        {
            if (_overlay == null) return;
            double now = _clock.Elapsed.TotalSeconds;
            double dt = now - _lastFrame;
            _lastFrame = now;

            _demo?.Tick();
            CountFrame(now);

            if (_settings.AutoMode)
                _auto.Update(_proc.GetVariance(), _settings.AutoVarianceThreshold, dt);

            var (lat, lon) = _proc.GetOutput();
            double scale = _settings.SensitivityScale;
            // Default signs (see MotionProcessor): left turn -> lat > 0 -> dots right (+x);
            // accelerating -> lon > 0 -> dots down (+y, WPF y grows downward).
            double signX = _settings.InvertX ? -1 : 1;
            if (_settings.FlowMode)
            {
                // Integrate lateral accel into a scroll position; the overlay wraps it inside the band.
                _flowX += _settings.FlowSpeedX * scale * lat * signX * Math.Min(dt, 0.1);
                _overlay.TargetX = _flowX;
            }
            else
            {
                _overlay.TargetX = _settings.TurnGain * scale * lat * signX;
            }
            _overlay.TargetY = _settings.VerticalGain * scale * lon * (_settings.InvertZ ? -1 : 1);
            _overlay.MotionX = scale * lat * signX;
            _overlay.MotionY = scale * lon * (_settings.InvertZ ? -1 : 1);
            _overlay.DotsVisible = WantDots;
            _overlay.Step(dt);

            if (_settings.ShowDebug && now - _lastDebug > 0.1)
            {
                _lastDebug = now;
                _overlay.SetDebugText(BuildDebugText());
            }

            // Speed up at once; slow down only after the lower rate has been enough for half a second.
            int want = FrameMsFor(_overlay.ScreenSpeed, _overlay.IsSettled);
            _slowerFor = want > _frameMs ? _slowerFor + dt : 0;
            if (want < _frameMs || _slowerFor > 0.5)
            {
                _slowerFor = 0;
                if (want != _frameMs) { _frameMs = want; UpdateRenderHook(); }
            }

            if (_overlay.IsIdle && !_settings.ShowDebug && !_settings.AutoMode && _demo == null)
                UpdateRenderHook();
        }

        /// <summary>
        /// Frame interval for the fastest on-screen motion: keep each step at about 2 DIP so motion stays
        /// smooth, and only use every display frame when that is needed.
        /// </summary>
        private static int FrameMsFor(double speed, bool settled) =>
            settled ? 100 : speed >= 120 ? VsyncMs : speed >= 60 ? 33 : speed >= 40 ? 50 : 66;

        private void CountFrame(double now)
        {
            _frames++;
            if (now - _fpsStart < 1) return;
            _fps = _frames / (now - _fpsStart);
            _frames = 0;
            _fpsStart = now;
            if (_perfLog == null) return;
            try
            {
                System.IO.File.AppendAllText(_perfLog,
                    $"{DateTime.Now:HH:mm:ss} fps {_fps:0.0} capture {_overlay?.CaptureMs ?? 0:0.00} ms particles {_overlay?.ParticleCount ?? 0} frame {_frameMs} ms speed {_overlay?.ScreenSpeed ?? 0:0} settled {_overlay?.IsSettled} motion {_overlay?.MotionX:+0.000;-0.000} {_overlay?.MotionY:+0.000;-0.000} strength {_overlay?.FountainIntensity:0.00}\n");
            }
            catch (System.IO.IOException) { }
        }

        private string BuildDebugText()
        {
            var d = _proc.GetDebug();
            string src = _demo != null ? "DEMO" : _sensor != null ? $"sensor {_sensor.ActualReportInterval} ms (~{d.RateHz:0} Hz)" : "no sensor";
            string auto = _settings.AutoMode ? (_auto.Active ? "auto: ACTIVE" : "auto: calm") : "auto: off";
            return
                $"src  {src}   fps {_fps:0}\n" +
                $"raw  X {d.RawX,7:+0.000;-0.000}  Y {d.RawY,7:+0.000;-0.000}  Z {d.RawZ,7:+0.000;-0.000}\n" +
                $"grav X {d.GravX,7:+0.000;-0.000}  Y {d.GravY,7:+0.000;-0.000}  Z {d.GravZ,7:+0.000;-0.000}\n" +
                $"lin  X {d.LinX,7:+0.000;-0.000}  Y {d.LinY,7:+0.000;-0.000}  Z {d.LinZ,7:+0.000;-0.000}\n" +
                $"out  lat {d.Lateral,7:+0.000;-0.000}  lon {d.Longitudinal,7:+0.000;-0.000} g\n" +
                $"var  {d.Variance:0.00000} g²  (thr {_settings.AutoVarianceThreshold:0.00000})  {auto}\n" +
                $"sens {_settings.Sensitivity}  invX {_settings.InvertX}  invZ {_settings.InvertZ}  flow {_settings.FlowMode}\n" +
                $"dots opacity {_settings.DotOpacity:0.00}  inverse colour {(_overlay?.ColorSampling == true ? "on" : "OFF (capture exclusion failed)")}  capture {_overlay?.CaptureMs ?? 0:0.0} ms\n" +
                $"fountain {(_settings.FountainEnabled ? "on" : "off")}  strength {_overlay?.FountainIntensity ?? 0:0.00}  particles {_overlay?.ParticleCount ?? 0}  dim {(_settings.DimMode ? $"{_settings.DimAmount * 100:0}% (table x{_dimmer?.GammaFactor:0.00}, colour x{_dimmer?.MatrixFactor:0.00})" : "off")}";
        }

        // ================================================================== actions

        private void ToggleDots()
        {
            _settings.ShowDots = !_settings.ShowDots;
            if (_settings.ShowDots && _settings.AutoMode) _auto.Reset();
            Changed();
        }

        /// <param name="fromSettingsWindow">Live edit from the settings window: skip the disk write
        /// (the window saves on close) and don't push values back into the control being dragged.</param>
        private void Changed(bool fromSettingsWindow = false)
        {
            if (!fromSettingsWindow)
            {
                _settings.Save();
                _settingsWindow?.Refresh();
            }
            _settings.ApplyTo(_proc);
            _overlay?.SetDebugVisible(_settings.ShowDebug);
            _overlay?.SettingsChanged();
            _dimmer?.Apply(_settings.DimMode, _settings.DimAmount);
            UpdateMenuChecks();
            UpdateRenderHook();
        }

        private void SetFlow(bool on, bool fromSettingsWindow)
        {
            _settings.FlowMode = on;
            // Drop the accumulated scroll so the spring doesn't race back across many bands.
            _flowX = 0;
            _overlay?.ResetX();
            Changed(fromSettingsWindow);
        }

        private void OpenSettings()
        {
            if (_settingsWindow != null)
            {
                if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
                _settingsWindow.Activate();
                return;
            }
            _settingsWindow = new SettingsWindow(new SettingsHost(
                Get: () => _settings,
                Changed: layout =>
                {
                    if (layout) _overlay?.ApplySettings(_settings);
                    Changed(fromSettingsWindow: true);
                },
                SetFlow: on => SetFlow(on, fromSettingsWindow: true),
                Calibrate: () => _proc.Calibrate(),
                Reset: ResetSettings));
            _settingsWindow.Closed += (_, _) =>
            {
                _settingsWindow = null;
                _settings.Save();
            };
            _settingsWindow.Show();
            _settingsWindow.Activate();
        }

        private void ResetSettings()
        {
            var defaults = new Settings { ShowDots = _settings.ShowDots };
            _settings.CopyFrom(defaults);
            _flowX = 0;
            _overlay?.ResetX();
            _overlay?.ApplySettings(_settings);
            Changed(fromSettingsWindow: true);
            _settings.Save();
        }

        private void SetDemo(bool on)
        {
            if (on && _demo == null)
            {
                // Real sensor (if any) keeps running but its samples would fight the demo; detach it.
                _sensor?.Dispose();
                _sensor = null;
                _proc.Calibrate();
                _demo = new DemoSource(_proc);
                _demoWindow = new DemoWindow(_demo);
                _demoWindow.Closed += (_, _) => { if (_demo != null) SetDemo(false); };
                _demoWindow.Show();
                var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                t.Tick += (_, _) => { if (_demoWindow == null) t.Stop(); else _demoWindow.UpdateStatus(); };
                t.Start();
            }
            else if (!on && _demo != null)
            {
                _demo = null;
                var w = _demoWindow; _demoWindow = null;
                w?.Close();
                _sensor = AccelerometerSource.TryCreate(_proc, _settings.ReportIntervalMs);
                _proc.Calibrate();
            }
            UpdateMenuChecks();
            UpdateRenderHook();
        }

        // ================================================================== tray

        private void CreateTray()
        {
            var menu = new Forms.ContextMenuStrip();

            var openSettingsWindow = new Forms.ToolStripMenuItem("Settings...", null, (_, _) => OpenSettings());
            openSettingsWindow.Font = new Font(openSettingsWindow.Font, System.Drawing.FontStyle.Bold);

            _miShow = new Forms.ToolStripMenuItem("Show dots", null, (_, _) => ToggleDots()) { ShortcutKeyDisplayString = "Ctrl+Alt+M" };
            _miInvX = new Forms.ToolStripMenuItem("Invert X (left/right)", null, (_, _) => { _settings.InvertX = !_settings.InvertX; Changed(); });
            _miFlow = new Forms.ToolStripMenuItem("Flow mode (scroll through turns)", null, (_, _) => SetFlow(!_settings.FlowMode, fromSettingsWindow: false));
            _miInvZ = new Forms.ToolStripMenuItem("Invert Z (accelerate/brake)", null, (_, _) => { _settings.InvertZ = !_settings.InvertZ; Changed(); });

            _miDim = new Forms.ToolStripMenuItem("Dim mode (darken the screen)", null, (_, _) => { _settings.DimMode = !_settings.DimMode; Changed(); });

            var sens = new Forms.ToolStripMenuItem("Sensitivity");
            _miLow = new Forms.ToolStripMenuItem("Low", null, (_, _) => { _settings.Sensitivity = Sensitivity.Low; Changed(); });
            _miMed = new Forms.ToolStripMenuItem("Medium", null, (_, _) => { _settings.Sensitivity = Sensitivity.Medium; Changed(); });
            _miHigh = new Forms.ToolStripMenuItem("High", null, (_, _) => { _settings.Sensitivity = Sensitivity.High; Changed(); });
            sens.DropDownItems.AddRange(new Forms.ToolStripItem[] { _miLow, _miMed, _miHigh });

            var cal = new Forms.ToolStripMenuItem("Calibrate (reset gravity)", null, (_, _) =>
            {
                _proc.Calibrate();
                _tray?.ShowBalloonTip(1500, "Motion Cues", "Gravity reset to the current reading.", Forms.ToolTipIcon.Info);
            });
            _miAuto = new Forms.ToolStripMenuItem("Auto show/hide when moving", null, (_, _) =>
            {
                _settings.AutoMode = !_settings.AutoMode;
                _auto.Reset();
                Changed();
            });
            _miDebug = new Forms.ToolStripMenuItem("Debug readout", null, (_, _) => { _settings.ShowDebug = !_settings.ShowDebug; Changed(); });
            _miDemo = new Forms.ToolStripMenuItem("Demo mode", null, (_, _) => SetDemo(_demo == null));
            var openSettings = new Forms.ToolStripMenuItem("Open settings file", null, (_, _) =>
            {
                _settings.Save();
                try { Process.Start(new ProcessStartInfo(Settings.FilePath) { UseShellExecute = true }); }
                catch (Exception ex) { MessageBox.Show(ex.Message, "Motion Cues"); }
            });
            var reload = new Forms.ToolStripMenuItem("Reload settings file", null, (_, _) =>
            {
                _settings = Settings.Load();
                _auto.CalmSeconds = _settings.AutoCalmSeconds;
                _overlay?.ApplySettings(_settings);
                Changed();
            });
            var exit = new Forms.ToolStripMenuItem("Exit", null, (_, _) => Shutdown());

            menu.Items.AddRange(new Forms.ToolStripItem[]
            {
                openSettingsWindow, _miShow, _miDim, new Forms.ToolStripSeparator(),
                _miInvX, _miInvZ, _miFlow, sens, cal, new Forms.ToolStripSeparator(),
                _miAuto, _miDebug, _miDemo, new Forms.ToolStripSeparator(),
                openSettings, reload, new Forms.ToolStripSeparator(),
                exit,
            });

            _trayIcon = MakeTrayIcon(out _trayHIcon);
            _tray = new Forms.NotifyIcon
            {
                Icon = _trayIcon,
                Text = "Motion Cues",
                ContextMenuStrip = menu,
                Visible = true,
            };
            _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ToggleDots(); };
        }

        private void UpdateMenuChecks()
        {
            if (_miShow == null) return;
            _miShow.Checked = _settings.ShowDots;
            _miInvX!.Checked = _settings.InvertX;
            _miInvZ!.Checked = _settings.InvertZ;
            _miFlow!.Checked = _settings.FlowMode;
            _miDim!.Checked = _settings.DimMode;
            _miLow!.Checked = _settings.Sensitivity == Sensitivity.Low;
            _miMed!.Checked = _settings.Sensitivity == Sensitivity.Medium;
            _miHigh!.Checked = _settings.Sensitivity == Sensitivity.High;
            _miAuto!.Checked = _settings.AutoMode;
            _miDebug!.Checked = _settings.ShowDebug;
            _miDemo!.Checked = _demo != null;
        }

        /// <summary>Draws the tray icon at runtime (two columns of dots) so no .ico resource is needed.</summary>
        private static Icon MakeTrayIcon(out IntPtr hIcon)
        {
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(System.Drawing.Color.Transparent);
                using var fill = new SolidBrush(System.Drawing.Color.FromArgb(255, 60, 60, 60));
                using var pen = new System.Drawing.Pen(System.Drawing.Color.White, 1.6f);
                for (int i = 0; i < 3; i++)
                {
                    foreach (float x in new[] { 4f, 20f })
                    {
                        float y = 2 + i * 10 + (x > 10 ? 4 : 0);
                        g.FillEllipse(fill, x, y, 8, 8);
                        g.DrawEllipse(pen, x, y, 8, 8);
                    }
                }
            }
            hIcon = bmp.GetHicon();
            return Icon.FromHandle(hIcon);
        }

        private static string Truncate(string s) => s.Length > 63 ? s.Substring(0, 63) : s;
    }
}
