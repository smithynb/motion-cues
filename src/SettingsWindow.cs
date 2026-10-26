using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MotionCues
{
    /// <summary>What the settings window needs from the app. Settings are read through <see cref="Get"/>
    /// because "Reload settings file" can swap the instance.</summary>
    public sealed record SettingsHost(
        Func<Settings> Get,
        Action<bool> Changed,      // arg: dot layout changed (overlay must rebuild)
        Action<bool> SetFlow,
        Action Calibrate,
        Action Reset);

    /// <summary>
    /// Tray > Settings. Every control applies live so the effect can be judged while riding;
    /// the app saves to disk when the window closes.
    /// </summary>
    public sealed class SettingsWindow : Window
    {
        private readonly SettingsHost _host;
        private readonly List<Action> _refreshers = new();
        private readonly FrameworkElement _flowRow, _turnGainRow;
        private bool _loading;

        private static readonly Brush HintBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0x6B, 0x6B));

        public SettingsWindow(SettingsHost host)
        {
            _host = host;
            Title = "Motion Cues Settings";
            Width = 460;
            SizeToContent = SizeToContent.Height;
            MaxHeight = SystemParameters.WorkArea.Height - 40;
            ResizeMode = ResizeMode.CanMinimize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 13;

            var p = new StackPanel { Margin = new Thickness(18, 10, 18, 14) };

            AddHeader(p, "Turns (left / right)");
            AddCheckBox(p, "Flow mode: scroll steadily through turns",
                "On: a steady turn scrolls the dots at a steady speed, and short jolts barely move them. " +
                "Off: the dots shift by an amount and spring back.",
                s => s.FlowMode, (_, v) => _host.SetFlow(v));
            _flowRow = AddSlider(p, "Turn scroll speed", "How fast the dots scroll during a turn.",
                100, 1500, 50, s => s.FlowSpeedX, (s, v) => s.FlowSpeedX = v, v => $"{v:0} px/s per g");
            _turnGainRow = AddSlider(p, "Turn strength", "How far the dots shift in a turn.",
                100, 2000, 50, s => s.TurnGain, (s, v) => s.TurnGain = v, v => $"{v:0} px per g");
            AddSlider(p, "Turn hold time",
                "How long a steady turn keeps showing before it is treated as the new level. " +
                "Longer holds long bends but recovers more slowly when you tilt the lid.",
                1, 15, 0.5, s => s.GravityTauSeconds, (s, v) => s.GravityTauSeconds = v, v => $"{v:0.0} s");

            AddHeader(p, "Speeding up / braking (up / down)");
            AddSlider(p, "Up/down strength", "How far the dots shift when the car speeds up or brakes.",
                0, 1000, 25, s => s.VerticalGain, (s, v) => s.VerticalGain = v, v => $"{v:0} px per g");

            AddHeader(p, "Feel");
            var sens = new ComboBox { Margin = new Thickness(0, 2, 0, 0), Width = 140, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var name in new[] { "Low (0.6x)", "Medium (1x)", "High (1.5x)" }) sens.Items.Add(name);
            sens.SelectionChanged += (_, _) =>
            {
                if (_loading || sens.SelectedIndex < 0) return;
                _host.Get().Sensitivity = (Sensitivity)sens.SelectedIndex;
                _host.Changed(false);
            };
            _refreshers.Add(() => sens.SelectedIndex = (int)_host.Get().Sensitivity);
            AddRow(p, "Sensitivity", "Multiplies all the strengths above. Same as the tray menu.", sens);
            AddSlider(p, "Ease speed", "How quickly the dots catch up. Lower is smoother and slower.",
                5, 150, 1, s => s.SpringStiffness, (s, v) => s.SpringStiffness = v, v => $"{v:0}");
            AddSlider(p, "Springiness", "0% settles with no bounce. Higher lets the dots overshoot a little.",
                0, 100, 5,
                s => Math.Clamp((1 - s.SpringDampingRatio) / 0.7, 0, 1) * 100,
                (s, v) => s.SpringDampingRatio = 1 - 0.7 * v / 100,
                v => $"{v:0}%");
            AddSlider(p, "Jolt filter", "Softens sharp bumps before they reach the dots. Too high hides short motions.",
                0.05, 1, 0.05, s => s.SmoothTauSeconds, (s, v) => s.SmoothTauSeconds = v, v => $"{v:0.00} s");
            AddSlider(p, "Ignore small motion", "Motion below this is treated as none. Raise it if the dots twitch on rough roads.",
                0, 0.08, 0.005, s => s.DeadzoneG, (s, v) => s.DeadzoneG = v, v => $"{v:0.000} g");

            AddHeader(p, "Dots");
            AddSlider(p, "Dots per side", null, 3, 15, 1,
                s => s.DotsPerSide, (s, v) => s.DotsPerSide = (int)Math.Round(v), v => $"{v:0}", layout: true);
            AddSlider(p, "Dot size", null, 6, 40, 1,
                s => s.DotDiameter, (s, v) => s.DotDiameter = v, v => $"{v:0} px", layout: true);
            AddSlider(p, "Opacity", "Lower values blend the dots toward grey.", 0.1, 1, 0.05,
                s => s.DotOpacity, (s, v) => s.DotOpacity = v, v => $"{v * 100:0}%");
            AddSlider(p, "Distance from screen edge", null, 20, 300, 5,
                s => s.EdgeMargin, (s, v) => s.EdgeMargin = v, v => $"{v:0} px", layout: true);

            AddHeader(p, "Particle fountain");
            AddCheckBox(p, "Particle fountain during steady motion",
                "In a long turn, speed-up or brake, extra dots of random size stream the way the dots move. " +
                "Standing still only shows the regular dots.",
                s => s.FountainEnabled, (s, v) => { s.FountainEnabled = v; _host.Changed(false); });
            AddSlider(p, "Amount", "How many particles appear each second on each side.",
                5, 150, 5, s => s.FountainAmount, (s, v) => s.FountainAmount = v, v => $"{v:0} per s");
            AddSlider(p, "Speed", null,
                50, 800, 10, s => s.FountainSpeed, (s, v) => s.FountainSpeed = v, v => $"{v:0} px/s");
            AddSlider(p, "Starts at", "How strong the steady motion must be before particles appear. Raise it for fewer false starts.",
                0, 0.2, 0.005, s => s.FountainStartG, (s, v) => s.FountainStartG = v, v => $"{v:0.000} g");
            AddSlider(p, "Spread", "How far particles may veer from the main direction.",
                0, 90, 5, s => s.FountainSpreadDegrees, (s, v) => s.FountainSpreadDegrees = v, v => $"±{v:0}°");
            AddSlider(p, "Area width", "Width of the strip around each column where particles fly.",
                100, 600, 10, s => s.FountainWidth, (s, v) => s.FountainWidth = v, v => $"{v:0} px");
            AddSlider(p, "Smallest particle", null,
                2, 40, 1, s => s.ParticleSizeMin, (s, v) => s.ParticleSizeMin = v, v => $"{v:0} px");
            AddSlider(p, "Largest particle", null,
                2, 40, 1, s => s.ParticleSizeMax, (s, v) => s.ParticleSizeMax = v, v => $"{v:0} px");

            AddHeader(p, "Screen");
            AddCheckBox(p, "Dim mode",
                "Darkens the display itself, cursor and taskbar included, past the lowest brightness setting. " +
                "Works like the brightness and contrast sliders in the AMD or NVIDIA control panel. Same as the tray menu.",
                s => s.DimMode, (s, v) => { s.DimMode = v; _host.Changed(false); });
            AddSlider(p, "Dim amount", "How much less light the screen gives off.",
                0.1, 0.95, 0.05, s => s.DimAmount, (s, v) => s.DimAmount = v, v => $"{v * 100:0}% darker");

            AddHeader(p, "Other");
            AddCheckBox(p, "Invert left/right", null, s => s.InvertX, (s, v) => { s.InvertX = v; _host.Changed(false); });
            AddCheckBox(p, "Invert speeding up/braking", null, s => s.InvertZ, (s, v) => { s.InvertZ = v; _host.Changed(false); });
            AddCheckBox(p, "Debug readout", "Shows raw sensor values in the top-right corner.",
                s => s.ShowDebug, (s, v) => { s.ShowDebug = v; _host.Changed(false); });
            AddCheckBox(p, "Start with Windows", null, _ => AutoStart.IsEnabled, (_, v) => AutoStart.Set(v));

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
            buttons.Children.Add(MakeButton("Calibrate", "Reset the level to the current position. Use when stopped or driving straight.",
                () => _host.Calibrate()));
            buttons.Children.Add(MakeButton("Reset to defaults", null, () =>
            {
                var r = MessageBox.Show(this, "Reset every setting to its default?", "Motion Cues",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (r == MessageBoxResult.OK) { _host.Reset(); Refresh(); }
            }));
            buttons.Children.Add(MakeButton("Close", null, Close));
            p.Children.Add(buttons);

            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = p };
            Refresh();
        }

        /// <summary>Re-read every control from the current settings (after reset, reload or a tray change).</summary>
        public void Refresh()
        {
            _loading = true;
            foreach (var r in _refreshers) r();
            bool flow = _host.Get().FlowMode;
            _flowRow.Visibility = flow ? Visibility.Visible : Visibility.Collapsed;
            _turnGainRow.Visibility = flow ? Visibility.Collapsed : Visibility.Visible;
            _loading = false;
        }

        // ------------------------------------------------------------------ builders

        private static void AddHeader(Panel p, string text) =>
            p.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 14, 0, 2),
            });

        private static FrameworkElement AddRow(Panel p, string label, string? hint, UIElement control)
        {
            var row = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            row.Children.Add(new TextBlock { Text = label });
            if (hint != null)
                row.Children.Add(new TextBlock { Text = hint, FontSize = 11.5, Foreground = HintBrush, TextWrapping = TextWrapping.Wrap });
            row.Children.Add(control);
            p.Children.Add(row);
            return row;
        }

        private FrameworkElement AddSlider(Panel p, string label, string? hint, double min, double max, double step,
            Func<Settings, double> get, Action<Settings, double> set, Func<double, string> format, bool layout = false)
        {
            var slider = new Slider
            {
                Minimum = min,
                Maximum = max,
                TickFrequency = step,
                IsSnapToTickEnabled = true,
                SmallChange = step,
                LargeChange = step * 4,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var value = new TextBlock { Width = 110, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(value, 1);
            grid.Children.Add(slider);
            grid.Children.Add(value);

            slider.ValueChanged += (_, e) =>
            {
                value.Text = format(e.NewValue);
                if (_loading) return;
                set(_host.Get(), e.NewValue);
                _host.Changed(layout);
            };
            _refreshers.Add(() =>
            {
                double v = Math.Clamp(get(_host.Get()), min, max);
                slider.Value = v;
                value.Text = format(v);
            });
            return AddRow(p, label, hint, grid);
        }

        private void AddCheckBox(Panel p, string label, string? hint, Func<Settings, bool> get, Action<Settings, bool> set)
        {
            var box = new CheckBox { Content = label, Margin = new Thickness(0, 6, 0, 0) };
            box.Click += (_, _) =>
            {
                set(_host.Get(), box.IsChecked == true);
                Refresh();
            };
            _refreshers.Add(() => box.IsChecked = get(_host.Get()));
            p.Children.Add(box);
            if (hint != null)
                p.Children.Add(new TextBlock
                {
                    Text = hint,
                    FontSize = 11.5,
                    Foreground = HintBrush,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(20, 0, 0, 0),
                });
        }

        private static Button MakeButton(string text, string? tip, Action click)
        {
            var b = new Button { Content = text, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0), ToolTip = tip };
            b.Click += (_, _) => click();
            return b;
        }
    }

    /// <summary>"Start with Windows" via the per-user Run key (no admin needed).</summary>
    internal static class AutoStart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "MotionCues";

        private static string Command => $"\"{Environment.ProcessPath}\"";

        public static bool IsEnabled
        {
            get
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
                return k?.GetValue(ValueName) is string;
            }
        }

        public static void Set(bool on)
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
                if (on) k.SetValue(ValueName, Command);
                else k.DeleteValue(ValueName, false);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not change the startup setting: " + ex.Message, "Motion Cues");
            }
        }

        /// <summary>If the exe was moved since startup was enabled, point the entry at the new path.</summary>
        public static void RefreshPath()
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                if (k?.GetValue(ValueName) is string cur && cur != Command) k.SetValue(ValueName, Command);
            }
            catch { /* not worth bothering the user about */ }
        }
    }
}
