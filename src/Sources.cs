using System;
using System.Diagnostics;
using Windows.Devices.Sensors;

namespace MotionCues
{
    /// <summary>
    /// Wraps Windows.Devices.Sensors.Accelerometer and feeds readings into a <see cref="MotionProcessor"/>.
    /// ReadingChanged fires on a sensor thread; the processor is thread-safe.
    /// </summary>
    public sealed class AccelerometerSource : IDisposable
    {
        private readonly Accelerometer _accel;
        private readonly MotionProcessor _proc;
        private DateTimeOffset? _lastTs;

        public uint ActualReportInterval => _accel.ReportInterval;
        public uint MinimumReportInterval => _accel.MinimumReportInterval;
        public string DeviceId => SafeDeviceId();

        private AccelerometerSource(Accelerometer a, MotionProcessor p, int requestedIntervalMs)
        {
            _accel = a;
            _proc = p;
            // Must be >= MinimumReportInterval (16 ms on the AMD SFH). 0 would mean "driver default" (100 ms).
            uint wanted = (uint)Math.Max(requestedIntervalMs, 1);
            _accel.ReportInterval = Math.Max(_accel.MinimumReportInterval, wanted);
            _accel.ReadingChanged += OnReading;
        }

        /// <summary>Returns null if the system has no accelerometer.</summary>
        public static AccelerometerSource? TryCreate(MotionProcessor p, int requestedIntervalMs)
        {
            try
            {
                var a = Accelerometer.GetDefault();
                return a == null ? null : new AccelerometerSource(a, p, requestedIntervalMs);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Accelerometer init failed: " + ex);
                return null;
            }
        }

        private void OnReading(Accelerometer sender, AccelerometerReadingChangedEventArgs args)
        {
            var r = args.Reading;
            // Use the sensor's own timestamp for dt so jitter in event delivery doesn't distort the filters.
            double dt = _lastTs.HasValue ? (r.Timestamp - _lastTs.Value).TotalSeconds : 0.016;
            _lastTs = r.Timestamp;
            _proc.AddSample(r.AccelerationX, r.AccelerationY, r.AccelerationZ, dt);
        }

        private string SafeDeviceId()
        {
            try { return _accel.DeviceId; } catch { return "?"; }
        }

        public void Dispose()
        {
            _accel.ReadingChanged -= OnReading;
            try { _accel.ReportInterval = 0; } catch { /* restore driver default; ignore */ }
        }
    }

    /// <summary>
    /// Fake sensor for testing without hardware. Produces raw readings in the same Windows convention
    /// (reading = gravity - kinematic accel) using the user's measured at-rest lid pose, so the full
    /// pipeline (gravity filter, tilt compensation, ...) is exercised.
    ///
    /// Input is either a scripted "drive" loop or manual values from the demo window's arrow keys.
    /// Vehicle frame: lateral > 0 = car accelerating to the LEFT (i.e. turning left);
    ///                forward > 0 = car speeding up.
    /// </summary>
    public sealed class DemoSource
    {
        private readonly MotionProcessor _proc;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _lastT;

        // Measured at rest: X=0.055, Y=-1.121, Z=-0.551 (lid tilted back ~25 deg).
        private const double G0X = 0.055, G0Y = -1.121, G0Z = -0.551;

        public bool Synthetic { get; set; } = true;
        /// <summary>Manual inputs in g (set by arrow keys).</summary>
        public double ManualLateral, ManualForward;
        public double CurrentLateral { get; private set; }
        public double CurrentForward { get; private set; }

        private readonly Random _rng = new Random(1);

        public DemoSource(MotionProcessor p) { _proc = p; }

        /// <summary>Call regularly (~60 Hz) from a timer.</summary>
        public void Tick()
        {
            double t = _clock.Elapsed.TotalSeconds;
            double dt = t - _lastT;
            _lastT = t;
            if (dt <= 0) return;

            double lat, fwd;
            if (Synthetic) (lat, fwd) = Script(t);
            else { lat = ManualLateral; fwd = ManualForward; }
            CurrentLateral = lat; CurrentForward = fwd;

            // Unit vectors in device coordinates (derived from the measured gravity direction):
            //  left  = -X
            //  forward = horizontal component of -Z (into the screen).
            double gm = Math.Sqrt(G0X * G0X + G0Y * G0Y + G0Z * G0Z);
            double ux = G0X / gm, uy = G0Y / gm, uz = G0Z / gm;     // "down" direction
            double fx = 0 - (-uz) * ux, fy = 0 - (-uz) * uy, fz = -1 - (-uz) * uz;   // -Z minus its vertical part
            double fm = Math.Sqrt(fx * fx + fy * fy + fz * fz);
            fx /= fm; fy /= fm; fz /= fm;

            // kinematic acceleration vector (device frame), in g
            double ax = -lat + fwd * fx;
            double ay = fwd * fy;
            double az = fwd * fz;

            // a little road noise so the deadzone/smoothing are visible
            double n() => (_rng.NextDouble() - 0.5) * 0.01;

            // Windows convention: reading = gravity - kinematic.
            _proc.AddSample(G0X - ax + n(), G0Y - ay + n(), G0Z - az + n(), dt);
        }

        /// <summary>A 24 s loop: accelerate, cruise, left turn, brake, right turn, stop-and-go.</summary>
        private static (double lat, double fwd) Script(double t)
        {
            double p = t % 24.0;
            double Bump(double start, double len) =>
                p < start || p > start + len ? 0 : Math.Sin(Math.PI * (p - start) / len);
            double fwd = 0.25 * Bump(1, 3)        // pull away
                       - 0.30 * Bump(8, 2.5)      // brake
                       + 0.15 * Bump(16, 1.5)     // stop-and-go
                       - 0.15 * Bump(18, 1.5)
                       + 0.10 * Bump(20, 1.5)
                       - 0.12 * Bump(22, 1.5);
            double lat = 0.25 * Bump(5, 2.5)      // left turn
                       - 0.25 * Bump(12, 3);      // right turn
            return (lat, fwd);
        }
    }
}
