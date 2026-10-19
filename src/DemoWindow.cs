using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MotionCues
{
    /// <summary>
    /// Small control window for demo mode. Either plays a scripted drive, or (manual mode) uses the
    /// arrow keys while this window is focused:
    ///   Left/Right = turn left/right, Up = accelerate, Down = brake.
    /// </summary>
    public sealed class DemoWindow : Window
    {
        private const double KeyG = 0.25;
        private readonly DemoSource _demo;
        private readonly RadioButton _synthetic, _manual;
        private readonly TextBlock _status;
        private bool _left, _right, _up, _down;

        public DemoWindow(DemoSource demo)
        {
            _demo = demo;
            Title = "Motion Cues - Demo mode";
            Width = 360; Height = 250;
            ResizeMode = ResizeMode.CanMinimize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;

            _synthetic = new RadioButton { Content = "Scripted drive (24 s loop)", IsChecked = demo.Synthetic, Margin = new Thickness(0, 4, 0, 0) };
            _manual = new RadioButton { Content = "Arrow keys (keep this window focused)", IsChecked = !demo.Synthetic, Margin = new Thickness(0, 4, 0, 0) };
            _synthetic.Checked += (_, _) => { _demo.Synthetic = true; Focus(); };
            _manual.Checked += (_, _) => { _demo.Synthetic = false; Focus(); };

            _status = new TextBlock { FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0, 10, 0, 0) };

            var help = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0),
                Foreground = Brushes.DimGray,
                Text = "←/→ turn left/right   ↑ accelerate   ↓ brake\n" +
                       "Expected: turn left → dots move right; accelerate → dots move down.",
            };

            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(new TextBlock { Text = "No real sensor input - synthetic motion.", FontWeight = FontWeights.SemiBold });
            panel.Children.Add(_synthetic);
            panel.Children.Add(_manual);
            panel.Children.Add(help);
            panel.Children.Add(_status);
            Content = panel;

            // Radio buttons would otherwise eat the arrow keys for focus navigation.
            PreviewKeyDown += OnKey;
            PreviewKeyUp += OnKey;
            Deactivated += (_, _) => { _left = _right = _up = _down = false; Apply(); };
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            bool down = e.IsDown;
            switch (e.Key)
            {
                case Key.Left: _left = down; break;
                case Key.Right: _right = down; break;
                case Key.Up: _up = down; break;
                case Key.Down: _down = down; break;
                default: return;
            }
            e.Handled = true;
            if (down && _demo.Synthetic) _manual.IsChecked = true; // pressing an arrow switches to manual
            Apply();
        }

        private void Apply()
        {
            _demo.ManualLateral = (_left ? KeyG : 0) - (_right ? KeyG : 0);
            _demo.ManualForward = (_up ? KeyG : 0) - (_down ? KeyG : 0);
        }

        public void UpdateStatus()
        {
            string lat = _demo.CurrentLateral > 0.01 ? "turning LEFT " : _demo.CurrentLateral < -0.01 ? "turning RIGHT" : "straight     ";
            string fwd = _demo.CurrentForward > 0.01 ? "ACCELERATING" : _demo.CurrentForward < -0.01 ? "BRAKING" : "cruising";
            _status.Text = $"Simulated: {lat}  {fwd}\nlat {_demo.CurrentLateral,6:+0.00;-0.00} g   fwd {_demo.CurrentForward,6:+0.00;-0.00} g";
        }
    }
}
