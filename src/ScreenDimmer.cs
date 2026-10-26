using System;
using System.IO;
using System.Text;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace MotionCues
{
    /// <summary>
    /// Dim mode: darkens the real display output, so the cursor, taskbar and every app dim too.
    ///
    /// First the graphics card's gamma table is scaled down. That is the same table the AMD / NVIDIA
    /// brightness and contrast sliders change, and it sits after everything else, cursor included.
    /// Windows refuses tables it thinks are too dark, so whatever it refuses is made up with the
    /// Magnification API's full-screen colour matrix. Both scale red, green and blue by the same factor,
    /// so colours keep their hue.
    ///
    /// The gamma table is only touched once the original has been read and saved next to the settings
    /// file, so a crash or a killed process can't lose it: the next start restores it. A restore only
    /// counts once reading the table back shows it took. HDR displays are not detected; Windows leaves
    /// gamma tables undefined there.
    /// </summary>
    internal sealed class ScreenDimmer : IDisposable
    {
        private static string Folder => Path.GetDirectoryName(Settings.FilePath)!;
        private static string BackupPath => Path.Combine(Folder, "gamma-backup.bin");
        private static string MatrixMarkerPath => Path.Combine(Folder, "colour-matrix-active");

        private string? _device;     // display the saved original belongs to
        private ushort[]? _original; // its table before we touched it (null = not touched)
        private ushort[]? _applied;  // what we last set, to notice when Windows or another app resets it
        private bool _magOn;
        private double _target = 1;  // wanted signal factor, 1 = off
        private double _pending = 1; // newest requested factor, applied after a short pause (slider drags)
        private double _floor;       // darkest factor the driver accepted for this _original
        private readonly DispatcherTimer _watch, _debounce;

        /// <summary>Signal factor the gamma table gives (1 = untouched).</summary>
        public double GammaFactor { get; private set; } = 1;
        /// <summary>Signal factor the colour matrix adds on top (1 = untouched).</summary>
        public double MatrixFactor { get; private set; } = 1;

        public ScreenDimmer()
        {
            // Display changes, sleep and other apps can reset the table; check and re-apply.
            _watch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _watch.Tick += (_, _) => Reassert();
            // Each try at a table can take a while, so a slider drag only applies where it stops.
            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _debounce.Tick += (_, _) => { _debounce.Stop(); ApplyPending(); };
            Microsoft.Win32.SystemEvents.SessionEnding += OnSessionEnding;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            RecoverFromCrash();
        }

        /// <param name="amount">0 to 0.95: how much less light the screen gives off.</param>
        public void Apply(bool on, double amount)
        {
            // Screens give off roughly signal^2.2, so cutting the light by `amount` scales the signal
            // by (1 - amount)^(1/2.2).
            _pending = on ? Math.Pow(1 - Math.Clamp(amount, 0, 0.95), 1 / 2.2) : 1;
            if (_pending >= 1) { _debounce.Stop(); ApplyPending(); } // turning off never waits
            else { _debounce.Stop(); _debounce.Start(); }
        }

        private void ApplyPending()
        {
            if (Math.Abs(_pending - _target) < 1e-4) return;
            _target = _pending;
            if (_target >= 1) Restore();
            else Dim();
        }

        private void Dim()
        {
            string device = PrimaryDevice();
            if (_original != null && device != _device) RestoreGamma(); // the primary display changed
            if (_original == null) TakeOriginal(device);

            GammaFactor = 1;
            if (_original != null)
            {
                IntPtr dc = CreateDc(_device!);
                try
                {
                    // Try the full amount, then search for the darkest table the driver accepts
                    // (remembered, so changing the amount doesn't search, and flicker, every time).
                    double g = Math.Max(_target, _floor);
                    if (!TrySet(dc, g))
                    {
                        double lo = _target, hi = 1;
                        for (int i = 0; i < 8; i++)
                        {
                            double mid = (lo + hi) / 2;
                            if (TrySet(dc, mid)) hi = mid; else lo = mid;
                        }
                        g = _floor = hi;
                        if (g >= 1) TrySet(dc, 1); // nothing was accepted: make sure no older table stays
                    }
                    GammaFactor = g;
                }
                finally { NativeMethods.DeleteDC(dc); }
            }

            SetMatrix(_target / GammaFactor);
            _watch.Start();
        }

        /// <summary>Read and back up the display's table. Without both, the table is left alone.</summary>
        private void TakeOriginal(string device)
        {
            var current = new ushort[768];
            IntPtr dc = CreateDc(device);
            bool read;
            try { read = NativeMethods.GetDeviceGammaRamp(dc, current); }
            finally { NativeMethods.DeleteDC(dc); }
            if (!read) return;
            current = Sane(current);
            if (!SaveBackup(device, current)) return;
            _device = device;
            _original = current;
            _floor = 0;
        }

        /// <summary>
        /// A table whose white is below 90% is almost certainly an earlier dim that was never undone
        /// (calibration tables keep white near full), so start from a normal table instead of keeping it.
        /// </summary>
        private static ushort[] Sane(ushort[] ramp) =>
            ramp[255] < 0.9 * 65535 && ramp[511] < 0.9 * 65535 && ramp[767] < 0.9 * 65535 ? Identity() : ramp;

        /// <summary>Set the original table scaled by <paramref name="g"/>; true if the driver took it.</summary>
        private bool TrySet(IntPtr dc, double g)
        {
            var ramp = new ushort[768];
            for (int i = 0; i < ramp.Length; i++) ramp[i] = (ushort)Math.Round(_original![i] * g);
            if (!SetAndCheck(dc, ramp)) return false;
            _applied = ramp;
            return true;
        }

        /// <summary>Windows rejects tables it thinks are unreadable silently, so read back to check.</summary>
        private static bool SetAndCheck(IntPtr dc, ushort[] ramp)
        {
            if (!NativeMethods.SetDeviceGammaRamp(dc, ramp)) return false;
            var back = new ushort[768];
            return NativeMethods.GetDeviceGammaRamp(dc, back) && NearlyEqual(back, ramp);
        }

        /// <summary>Put the original table back. Keeps it (and the backup) if the restore didn't take.</summary>
        private bool RestoreGamma()
        {
            if (_original == null) return true;
            IntPtr dc = CreateDc(_device!);
            bool ok;
            try { ok = SetAndCheck(dc, _original); }
            finally { NativeMethods.DeleteDC(dc); }
            if (!ok) return false;
            _original = _applied = null;
            _device = null;
            GammaFactor = 1;
            try { File.Delete(BackupPath); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Gamma backup delete failed: " + ex.Message); }
            return true;
        }

        private void SetMatrix(double m)
        {
            MatrixFactor = 1;
            try
            {
                if (m >= 0.999)
                {
                    if (!_magOn) return;
                    var identity = Effect(1);
                    NativeMethods.MagSetFullscreenColorEffect(ref identity);
                    NativeMethods.MagUninitialize();
                    _magOn = false;
                    try { File.Delete(MatrixMarkerPath); } catch (Exception) { }
                    return;
                }
                if (!_magOn)
                {
                    if (!NativeMethods.MagInitialize()) return;
                    // Don't replace a colour filter someone else set (Magnifier invert, grayscale...).
                    var existing = Effect(1);
                    if (NativeMethods.MagGetFullscreenColorEffect(ref existing) && !IsIdentity(existing))
                    {
                        NativeMethods.MagUninitialize();
                        return;
                    }
                    _magOn = true;
                    try { File.WriteAllBytes(MatrixMarkerPath, Array.Empty<byte>()); } catch (Exception) { }
                }
                var effect = Effect((float)m);
                if (NativeMethods.MagSetFullscreenColorEffect(ref effect)) MatrixFactor = m;
            }
            catch (Exception ex) // Magnification.dll missing or refusing; the gamma table alone still dims
            {
                System.Diagnostics.Debug.WriteLine("Colour matrix failed: " + ex.Message);
            }
        }

        /// <summary>5x5 colour matrix that scales R, G and B by <paramref name="m"/>.</summary>
        private static NativeMethods.MAGCOLOREFFECT Effect(float m)
        {
            var t = new float[25];
            t[0] = t[6] = t[12] = m;
            t[18] = t[24] = 1;
            return new NativeMethods.MAGCOLOREFFECT { transform = t };
        }

        private static bool IsIdentity(NativeMethods.MAGCOLOREFFECT e)
        {
            for (int i = 0; i < 25; i++)
                if (Math.Abs(e.transform[i] - (i % 6 == 0 ? 1 : 0)) > 1e-4) return false;
            return true;
        }

        private void Reassert()
        {
            if (_target >= 1)
            {
                // A restore that didn't take is retried until it does.
                if (RestoreGamma()) _watch.Stop();
                return;
            }
            if (_original == null || PrimaryDevice() != _device) { Dim(); return; }
            if (_applied == null) return;

            var current = new ushort[768];
            IntPtr dc = CreateDc(_device!);
            bool read;
            try { read = NativeMethods.GetDeviceGammaRamp(dc, current); }
            finally { NativeMethods.DeleteDC(dc); }
            if (!read || NearlyEqual(current, _applied)) return;

            // Someone reset the table (display change, sleep, another app): take theirs as the new base.
            current = Sane(current);
            if (!SaveBackup(_device!, current)) return;
            _original = current;
            _floor = 0;
            Dim();
        }

        /// <summary>Back to normal. The gamma table and the colour matrix are restored independently.</summary>
        private void Restore()
        {
            _target = _pending = 1;
            bool restored = false;
            try { restored = RestoreGamma(); }
            finally
            {
                SetMatrix(1);
                // Keep retrying a failed restore; the timer can only be touched on the UI thread.
                if (_watch.Dispatcher.CheckAccess())
                {
                    if (restored) _watch.Stop(); else _watch.Start();
                }
            }
        }

        private void RecoverFromCrash()
        {
            try
            {
                if (File.Exists(BackupPath) && ReadBackup(out var device, out var ramp))
                {
                    IntPtr dc = CreateDc(device);
                    bool ok;
                    try { ok = SetAndCheck(dc, Sane(ramp)); }
                    finally { NativeMethods.DeleteDC(dc); }
                    if (ok) File.Delete(BackupPath);
                }
                // The colour matrix may have outlived the process too.
                if (File.Exists(MatrixMarkerPath) && NativeMethods.MagInitialize())
                {
                    var identity = Effect(1);
                    NativeMethods.MagSetFullscreenColorEffect(ref identity);
                    NativeMethods.MagUninitialize();
                    File.Delete(MatrixMarkerPath);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Dim recovery failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Backup format: the display name, a newline, then the 768 table values. Written to a temp
        /// file and moved into place, so a crash mid-write never leaves a half backup.
        /// </summary>
        private static bool SaveBackup(string device, ushort[] ramp)
        {
            try
            {
                var name = Encoding.UTF8.GetBytes(device + "\n");
                var bytes = new byte[name.Length + 768 * 2];
                name.CopyTo(bytes, 0);
                Buffer.BlockCopy(ramp, 0, bytes, name.Length, 768 * 2);
                Directory.CreateDirectory(Folder);
                string tmp = BackupPath + ".tmp";
                using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                {
                    f.Write(bytes);
                    f.Flush(flushToDisk: true);
                }
                File.Move(tmp, BackupPath, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Gamma backup failed: " + ex.Message);
                return false;
            }
        }

        private static bool ReadBackup(out string device, out ushort[] ramp)
        {
            var bytes = File.ReadAllBytes(BackupPath);
            int nl = Array.IndexOf(bytes, (byte)'\n');
            device = nl > 0 ? Encoding.UTF8.GetString(bytes, 0, nl) : "";
            ramp = new ushort[768];
            if (nl <= 0 || bytes.Length != nl + 1 + 768 * 2) return false;
            Buffer.BlockCopy(bytes, nl + 1, ramp, 0, 768 * 2);
            return true;
        }

        private static ushort[] Identity()
        {
            var r = new ushort[768];
            for (int i = 0; i < 768; i++) r[i] = (ushort)((i % 256) * 257);
            return r;
        }

        private static string PrimaryDevice() => Forms.Screen.PrimaryScreen?.DeviceName ?? @"\\.\DISPLAY1";

        private static IntPtr CreateDc(string device) => NativeMethods.CreateDC(null, device, null, IntPtr.Zero);

        /// <summary>Equal within rounding (drivers may quantise the table).</summary>
        private static bool NearlyEqual(ushort[] a, ushort[] b)
        {
            for (int i = 0; i < a.Length; i++)
                if (Math.Abs(a[i] - b[i]) > 256) return false;
            return true;
        }

        private void OnSessionEnding(object? sender, Microsoft.Win32.SessionEndingEventArgs e)
        {
            try { Restore(); } catch (Exception) { /* best effort while the session ends */ }
        }

        private void OnProcessExit(object? sender, EventArgs e)
        {
            try { RestoreGamma(); } catch (Exception) { /* exiting anyway */ }
        }

        public void Dispose()
        {
            Restore();
            _debounce.Stop();
            Microsoft.Win32.SystemEvents.SessionEnding -= OnSessionEnding;
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        }
    }
}
