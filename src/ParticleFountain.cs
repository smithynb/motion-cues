using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MotionCues
{
    /// <summary>
    /// Extra dots for one side of the screen that stream in the direction the cue dots move while the
    /// motion is steady (a long turn, a long speed-up or brake). Each particle gets a random size,
    /// speed, spread angle and lifetime, so the stream looks irregular instead of a second grid.
    /// Particles are pooled Ellipses on the overlay canvas; dead ones are collapsed.
    /// </summary>
    internal sealed class ParticleFountain
    {
        public const int PoolSize = 160;

        private struct Particle
        {
            public double X, Y, Speed, Angle, Age, Life, Size;
            public bool Alive;
        }

        private readonly Particle[] _p = new Particle[PoolSize];
        private readonly Ellipse[] _els = new Ellipse[PoolSize];
        private readonly ImageBrush[] _brushes = new ImageBrush[PoolSize];
        private readonly Random _rng;
        private readonly bool _inverted;
        private double _spawnDebt;

        public int Count { get; private set; }

        public ParticleFountain(Canvas canvas, ImageSource? image, bool inverted, Brush fallbackFill, Brush fallbackStroke, int seed)
        {
            _rng = new Random(seed);
            _inverted = inverted;
            for (int i = 0; i < PoolSize; i++)
            {
                _brushes[i] = new ImageBrush(image) { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
                _els[i] = new Ellipse
                {
                    Fill = inverted ? _brushes[i] : fallbackFill,
                    Stroke = inverted ? null : fallbackStroke,
                    IsHitTestVisible = false,
                    Visibility = Visibility.Collapsed,
                };
                canvas.Children.Add(_els[i]);
            }
        }

        /// <summary>Kill every particle at once (dots hidden).</summary>
        public void Clear()
        {
            if (Count == 0) return;
            for (int i = 0; i < PoolSize; i++)
            {
                _p[i].Alive = false;
                _els[i].Visibility = Visibility.Collapsed;
            }
            Count = 0;
            _spawnDebt = 0;
        }

        public void SetImage(ImageSource image)
        {
            foreach (var b in _brushes) b.ImageSource = image;
        }

        /// <summary>
        /// Advance and redraw. <paramref name="region"/> is where particles live (screen DIP);
        /// (dirX, dirY) is the unit direction of the stream; intensity 0..1 scales the spawn rate;
        /// <paramref name="stripLeft"/> is the left edge of the strip window the canvas sits in.
        /// </summary>
        public void Step(double dt, Rect region, double dirX, double dirY, double intensity, double stripLeft, Settings s)
        {
            double baseAngle = Math.Atan2(dirY, dirX);
            double spread = s.FountainSpreadDegrees * Math.PI / 180;
            double minSize = Math.Min(s.ParticleSizeMin, s.ParticleSizeMax), maxSize = Math.Max(s.ParticleSizeMin, s.ParticleSizeMax);

            if (s.FountainEnabled && intensity > 0 && region.Width > 0 && region.Height > 0)
            {
                _spawnDebt += s.FountainAmount * intensity * dt;
                for (int i = 0; i < PoolSize && _spawnDebt >= 1; i++)
                {
                    if (_p[i].Alive) continue;
                    _spawnDebt -= 1;
                    _p[i] = new Particle
                    {
                        X = region.Left + _rng.NextDouble() * region.Width,
                        Y = region.Top + _rng.NextDouble() * region.Height,
                        Speed = s.FountainSpeed * (0.5 + _rng.NextDouble()) * (0.6 + 0.4 * intensity),
                        Angle = (_rng.NextDouble() * 2 - 1) * spread,
                        Life = 0.7 + _rng.NextDouble() * 1.1,
                        // Square the random value so small particles are more common than big ones.
                        Size = minSize + (maxSize - minSize) * Math.Pow(_rng.NextDouble(), 2),
                        Alive = true,
                    };
                    _els[i].Width = _els[i].Height = _p[i].Size;
                    _els[i].StrokeThickness = Math.Max(1.0, _p[i].Size / 9.0);
                    _els[i].Visibility = Visibility.Visible;
                }
            }
            _spawnDebt = Math.Min(_spawnDebt, 1); // don't burst after a pause or when the pool is full

            int count = 0;
            for (int i = 0; i < PoolSize; i++)
            {
                ref var p = ref _p[i];
                if (!p.Alive) continue;

                // Follow the current stream direction, so a change of motion bends the whole stream.
                double a = baseAngle + p.Angle;
                p.X += Math.Cos(a) * p.Speed * dt;
                p.Y += Math.Sin(a) * p.Speed * dt;
                p.Age += dt;

                double edge = Math.Max(p.Size * 1.5, 1);
                double fadeIn = p.Age / 0.2, fadeOut = (p.Life - p.Age) / 0.35;
                double ex = Math.Min(p.X - region.Left, region.Right - p.X) / edge;
                double ey = Math.Min(p.Y - region.Top, region.Bottom - p.Y) / edge;
                double opacity = Math.Min(Math.Min(fadeIn, fadeOut), Math.Min(ex, ey));
                if (p.Age >= p.Life || opacity <= 0 && p.Age > 0.2)
                {
                    p.Alive = false;
                    _els[i].Visibility = Visibility.Collapsed;
                    continue;
                }

                var el = _els[i];
                double left = p.X - p.Size / 2 - stripLeft;
                Canvas.SetLeft(el, left);
                Canvas.SetTop(el, p.Y - p.Size / 2);
                el.Opacity = Math.Clamp(opacity, 0, 1);
                if (_inverted) _brushes[i].Viewbox = new Rect(left, p.Y - p.Size / 2, p.Size, p.Size);
                count++;
            }
            Count = count;
        }
    }
}
