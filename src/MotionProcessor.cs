using System;
using System.Collections.Generic;

namespace MotionCues
{
    /// <summary>
    /// Platform-independent signal processing: raw accelerometer readings in, lateral/longitudinal
    /// vehicle acceleration (in g) out. No Windows dependencies so it can be unit-tested anywhere.
    ///
    /// Windows sensor convention (Windows.Devices.Sensors.Accelerometer), device frame:
    ///   +X = right along the screen, +Y = up along the screen, +Z = out of the screen toward the user.
    ///   reading = gravityVector - kinematicAcceleration   (at rest the "up" axis reads about -1 g)
    ///
    /// The laptop sits on the passenger's lap with the lid facing them, so the car's forward
    /// direction is INTO the screen (-Z) and +Z points backward in the car.
    ///
    /// Pipeline per sample:
    ///   1. gravity  = exponential low-pass of raw (tau ~2.5 s)      -- no gyro, so this is the only way
    ///   2. linear   = raw - gravity
    ///   3. (optional) tilt compensation: project linear onto the horizontal plane, i.e. onto the
    ///      device X and Z axes with their component along gravity removed. With the lid tilted back
    ///      ~25 deg, this keeps vertical bumps from leaking into the forward/back signal.
    ///   4. (optional) normalise by |gravity| so a sensor that reads 1.25 g at rest still reports
    ///      vehicle acceleration in true g.
    ///   5. short low-pass (tau ~150 ms), soft deadzone (~0.02 g), clamp (+-0.4 g).
    ///
    /// Outputs (before user inversion):
    ///   Lateral  = reading along device +X. Car turns LEFT -> centripetal accel points left (-X)
    ///              -> reading X = -(-a) = +a -> Lateral > 0 -> dots move RIGHT (dx = +gain * Lateral).
    ///   Longitudinal = reading along device +Z (backward). Car ACCELERATES -> accel points forward (-Z)
    ///              -> reading Z = +a -> Longitudinal > 0 -> dots move DOWN (screen +y, dy = +gain * Longitudinal).
    ///              Braking gives the opposite sign -> dots move UP.
    /// </summary>
    public sealed class MotionProcessor
    {
        // ---- tunables (copied from Settings) ----
        public double GravityTau = 2.5;     // s
        public double SmoothTau = 0.15;     // s
        public double DeadzoneG = 0.02;
        public double ClampG = 0.4;
        public bool TiltCompensation = true;
        public bool NormalizeByGravity = true;

        // ---- auto mode ----
        public double AutoWindowSeconds = 5.0;
        public double AutoVarianceThreshold = 0.0009; // g^2 (~0.03 g std-dev)

        // ---- state ----
        private bool _initialized;
        private double _gx, _gy, _gz;          // gravity estimate
        private double _sLat, _sLon;           // smoothed outputs
        private readonly object _lock = new object();
        private readonly Queue<(double t, double v)> _window = new Queue<(double, double)>();
        private double _winSum, _winSumSq;
        private double _time;                  // accumulated sample time, s

        // ---- latest values (for debug readout) ----
        public double RawX, RawY, RawZ;
        public double LinX, LinY, LinZ;
        public double Lateral, Longitudinal;   // final, in g
        public double Variance;                // of horizontal |linear| over the auto window
        public double SampleRateHz;
        private double _rateEstimate;

        /// <summary>Feed one raw reading (in g). dt = seconds since previous sample.</summary>
        public void AddSample(double x, double y, double z, double dt)
        {
            lock (_lock)
            {
                RawX = x; RawY = y; RawZ = z;
                if (!_initialized)
                {
                    _gx = x; _gy = y; _gz = z;
                    _initialized = true;
                    return;
                }

                // Sensor timestamps can jitter or jump (sleep/resume); keep dt sane.
                if (!(dt > 0)) dt = 0.016;
                if (dt > 0.5)
                {
                    // Long gap: re-seed gravity rather than integrating a huge step.
                    _gx = x; _gy = y; _gz = z;
                    _sLat = _sLon = 0;
                    return;
                }
                _time += dt;
                _rateEstimate += ((1.0 / dt) - _rateEstimate) * 0.05;
                SampleRateHz = _rateEstimate;

                // 1. gravity low-pass
                double aG = 1.0 - Math.Exp(-dt / Math.Max(0.05, GravityTau));
                _gx += (x - _gx) * aG;
                _gy += (y - _gy) * aG;
                _gz += (z - _gz) * aG;

                // 2. linear
                LinX = x - _gx; LinY = y - _gy; LinZ = z - _gz;

                double gMag = Math.Sqrt(_gx * _gx + _gy * _gy + _gz * _gz);
                double lat, lon;
                if (TiltCompensation && gMag > 0.3)
                {
                    // 3. horizontal axes: device X / Z with the gravity component removed.
                    double ux = _gx / gMag, uy = _gy / gMag, uz = _gz / gMag;
                    lat = ProjectHorizontal(1, 0, 0, ux, uy, uz);
                    lon = ProjectHorizontal(0, 0, 1, ux, uy, uz);
                }
                else
                {
                    lat = LinX;
                    lon = LinZ;
                }

                // 4. scale to true g if the sensor's 1 g isn't exactly 1.
                if (NormalizeByGravity && gMag > 0.5 && gMag < 2.0)
                {
                    lat /= gMag;
                    lon /= gMag;
                }

                // Auto-mode statistics on the unsmoothed horizontal magnitude.
                UpdateWindow(Math.Sqrt(lat * lat + lon * lon));

                // 5. smoothing, deadzone, clamp
                double aS = 1.0 - Math.Exp(-dt / Math.Max(0.005, SmoothTau));
                _sLat += (lat - _sLat) * aS;
                _sLon += (lon - _sLon) * aS;
                Lateral = Clamp(Deadzone(_sLat, DeadzoneG), ClampG);
                Longitudinal = Clamp(Deadzone(_sLon, DeadzoneG), ClampG);

                // Local helper: component of the linear accel along axis a with gravity removed.
                double ProjectHorizontal(double ax, double ay, double az, double gx, double gy, double gz)
                {
                    double d = ax * gx + ay * gy + az * gz;
                    double hx = ax - d * gx, hy = ay - d * gy, hz = az - d * gz;
                    double hm = Math.Sqrt(hx * hx + hy * hy + hz * hz);
                    if (hm < 1e-3) return 0; // axis is vertical; no horizontal information
                    return (LinX * hx + LinY * hy + LinZ * hz) / hm;
                }
            }
        }

        /// <summary>Snap the gravity estimate to the latest raw reading (use when the car is steady).</summary>
        public void Calibrate()
        {
            lock (_lock)
            {
                if (!_initialized) return;
                _gx = RawX; _gy = RawY; _gz = RawZ;
                _sLat = _sLon = 0;
                Lateral = Longitudinal = 0;
                LinX = LinY = LinZ = 0;
                _window.Clear(); _winSum = _winSumSq = 0; Variance = 0;
            }
        }

        public (double lat, double lon) GetOutput()
        {
            lock (_lock) return (Lateral, Longitudinal);
        }

        public DebugSnapshot GetDebug()
        {
            lock (_lock)
                return new DebugSnapshot(RawX, RawY, RawZ, _gx, _gy, _gz, LinX, LinY, LinZ,
                                         Lateral, Longitudinal, Variance, SampleRateHz);
        }

        public double GetVariance() { lock (_lock) return Variance; }

        private void UpdateWindow(double v)
        {
            _window.Enqueue((_time, v));
            _winSum += v; _winSumSq += v * v;
            while (_window.Count > 0 && _time - _window.Peek().t > AutoWindowSeconds)
            {
                var old = _window.Dequeue();
                _winSum -= old.v; _winSumSq -= old.v * old.v;
            }
            int n = _window.Count;
            Variance = n > 1 ? Math.Max(0, (_winSumSq - _winSum * _winSum / n) / (n - 1)) : 0;
        }

        private static double Deadzone(double v, double dz)
        {
            double a = Math.Abs(v) - dz;
            return a <= 0 ? 0 : Math.Sign(v) * a;
        }

        private static double Clamp(double v, double c) => v > c ? c : (v < -c ? -c : v);
    }

    public readonly record struct DebugSnapshot(
        double RawX, double RawY, double RawZ,
        double GravX, double GravY, double GravZ,
        double LinX, double LinY, double LinZ,
        double Lateral, double Longitudinal, double Variance, double RateHz);

    /// <summary>
    /// Auto show/hide: show when variance of linear accel over the window exceeds a threshold,
    /// hide after a period of calm. Pure logic; caller supplies the clock.
    /// </summary>
    public sealed class AutoVisibility
    {
        public double CalmSeconds = 30;
        public bool Active { get; private set; }
        private double _calmFor;

        /// <returns>true if <see cref="Active"/> changed.</returns>
        public bool Update(double variance, double threshold, double dt)
        {
            bool before = Active;
            if (variance > threshold)
            {
                Active = true;
                _calmFor = 0;
            }
            else if (Active)
            {
                _calmFor += dt;
                if (_calmFor >= CalmSeconds) Active = false;
            }
            return before != Active;
        }

        public void Reset() { Active = false; _calmFor = 0; }
    }
}
