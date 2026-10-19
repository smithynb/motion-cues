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
        private OverlayWindow? _overlay;
        private Forms.NotifyIcon? _tray;
        private Icon? _trayIcon;
        private IntPtr _trayHIcon;

        // menu items whose check state we update
        private Forms.ToolStripMenuItem? _miShow, _miInvX, _miInvZ, _miDebug, _miAuto, _miDemo, _miFlow;
        private Forms.ToolStripMenuItem? _miLow, _miMed, _miHigh;

        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _lastFrame;
        private double _lastDebug;
        private bool _renderHooked;
        private double _flowX;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += (_, ex) =>
            {
                Debug.WriteLine(ex.Exception);
                ex.Handled = true; // an overlay should never take the user's session down with it
            };

            _settings = Settings.Load();
            _settings.ApplyTo(_proc);
            AutoStart.RefreshPath();
            _auto.CalmSeconds = _settings.AutoCalmSeconds;

            _overlay = new OverlayWindow(_settings);
            _overlay.HotkeyPressed += ToggleDots;
            _overlay.Show();
            _overlay.ApplySettings(_settings);
            _overlay.SetDebugVisible(_settings.ShowDebug);

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
        }

        protected override void OnExit(ExitEventArgs e)
        {
            CompositionTarget.Rendering -= OnRendering;
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
        /// Hook CompositionTarget.Rendering only while something animates, so the app costs ~nothing
        /// when dots are hidden. In auto mode we keep a cheap timer-free check inside the frame loop,
        /// so the hook stays on while auto mode is armed.
        /// </summary>
        private void UpdateRenderHook()
        {
            bool need = _settings.ShowDots || _settings.ShowDebug || _demo != null || !(_overlay?.IsIdle ?? true);
            if (need && !_renderHooked)
            {
                _lastFrame = _clock.Elapsed.TotalSeconds;
                CompositionTarget.Rendering += OnRendering;
                _renderHooked = true;
            }
            else if (!need && _renderHooked)
            {
                CompositionTarget.Rendering -= OnRendering;
                _renderHooked = false;
            }
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            if (_overlay == null) return;
            double now = _clock.Elapsed.TotalSeconds;
            double dt = now - _lastFrame;
            _lastFrame = now;

            _demo?.Tick();

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
            _overlay.DotsVisible = WantDots;
            _overlay.Step(dt);

            if (_settings.ShowDebug && now - _lastDebug > 0.1)
            {
                _lastDebug = now;
                _overlay.SetDebugText(BuildDebugText());
            }

            if (_overlay.IsIdle && !_settings.ShowDebug && !_settings.AutoMode && _demo == null)
                UpdateRenderHook();
        }

        private string BuildDebugText()
        {
            var d = _proc.GetDebug();
            string src = _demo != null ? "DEMO" : _sensor != null ? $"sensor {_sensor.ActualReportInterval} ms (~{d.RateHz:0} Hz)" : "no sensor";
            string auto = _settings.AutoMode ? (_auto.Active ? "auto: ACTIVE" : "auto: calm") : "auto: off";
            return
                $"src  {src}\n" +
                $"raw  X {d.RawX,7:+0.000;-0.000}  Y {d.RawY,7:+0.000;-0.000}  Z {d.RawZ,7:+0.000;-0.000}\n" +
                $"grav X {d.GravX,7:+0.000;-0.000}  Y {d.GravY,7:+0.000;-0.000}  Z {d.GravZ,7:+0.000;-0.000}\n" +
                $"lin  X {d.LinX,7:+0.000;-0.000}  Y {d.LinY,7:+0.000;-0.000}  Z {d.LinZ,7:+0.000;-0.000}\n" +
                $"out  lat {d.Lateral,7:+0.000;-0.000}  lon {d.Longitudinal,7:+0.000;-0.000} g\n" +
                $"var  {d.Variance:0.00000} g²  (thr {_settings.AutoVarianceThreshold:0.00000})  {auto}\n" +
                $"sens {_settings.Sensitivity}  invX {_settings.InvertX}  invZ {_settings.InvertZ}  flow {_settings.FlowMode}";
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
                openSettingsWindow, _miShow, new Forms.ToolStripSeparator(),
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
