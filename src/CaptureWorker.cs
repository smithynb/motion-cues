using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MotionCues
{
    /// <summary>
    /// Grabs the screen under both dot strips on a background thread, inverted by the blit itself
    /// (NOTSRCCOPY), and hands the result to the UI thread's bitmaps.
    ///
    /// A blit from the screen waits for the compositor's next frame (about 16 ms at 60 Hz), because
    /// Windows has to compose the screen without our capture-excluded windows. Doing it on the UI
    /// thread would stall the dots, so this thread does it: one blit per strip, into a back buffer
    /// that is swapped with the front buffer under a lock. The UI thread copies only rows that changed
    /// into WPF, so a still screen causes no redraw at all. (One blit spanning both strips was tried:
    /// reading the whole screen width cost more CPU and frames than the second wait.)
    /// </summary>
    internal sealed unsafe class CaptureWorker : IDisposable
    {
        private const int ChunkRows = 64; // granularity of the change check and dirty rects

        private sealed class Buffer
        {
            public IntPtr Dc, Dib, OldBitmap, Bits;
            public int W, H;
        }

        private sealed class Side
        {
            public readonly Buffer[] Buffers = { new Buffer(), new Buffer() };
            public int Back;                         // worker writes here; the other one is the front
            public bool Ready;                       // front holds a capture the UI hasn't taken yet
            public System.Drawing.Rectangle Request; // screen rect in physical pixels (UI -> worker)
            public WriteableBitmap? Image;           // UI thread only
        }

        private readonly Side[] _sides = { new Side(), new Side() };
        private readonly object _lock = new();
        private readonly AutoResetEvent _wake = new(false);
        private readonly Thread _thread;
        private volatile bool _stop, _enabled;
        private volatile int _intervalMs = 66;
        private double _dpiScale = 1;

        /// <summary>Time the last capture of both strips took (mostly waiting for the compositor).</summary>
        public double LastMs { get; private set; }

        public CaptureWorker()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "Motion Cues capture", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        /// <summary>Set from the UI thread: where to capture, how often, and whether at all.</summary>
        public void Configure(System.Drawing.Rectangle left, System.Drawing.Rectangle right, double dpiScale, int intervalMs, bool enabled)
        {
            lock (_lock)
            {
                _sides[0].Request = left;
                _sides[1].Request = right;
                _dpiScale = dpiScale;
            }
            bool wake = (enabled && !_enabled) || intervalMs < _intervalMs;
            _intervalMs = intervalMs;
            _enabled = enabled;
            if (wake) _wake.Set();
        }

        /// <summary>The bitmap the dots of a side should read from (UI thread).</summary>
        public WriteableBitmap? Image(int side) => _sides[side].Image;

        /// <summary>
        /// UI thread: move any finished capture into the WPF bitmaps. Returns a bit per side whose bitmap
        /// was replaced (new size), so the caller can point its brushes at it.
        /// </summary>
        public int Apply()
        {
            int replaced = 0;
            lock (_lock)
            {
                for (int s = 0; s < 2; s++)
                {
                    var side = _sides[s];
                    if (!side.Ready) continue;
                    var front = side.Buffers[1 - side.Back];
                    if (side.Image == null || side.Image.PixelWidth != front.W || side.Image.PixelHeight != front.H)
                    {
                        // DPI matches the screen so one bitmap pixel is one device pixel and brush Viewboxes are in DIPs.
                        side.Image = new WriteableBitmap(front.W, front.H, 96 * _dpiScale, 96 * _dpiScale, PixelFormats.Bgr32, null);
                        replaced |= 1 << s;
                    }
                    // The render thread may hold the bitmap; try again next frame instead of blocking.
                    if (!side.Image.TryLock(new Duration(TimeSpan.Zero))) continue;
                    try { CopyChangedRows(front, side.Image); }
                    finally { side.Image.Unlock(); }
                    side.Ready = false;
                }
            }
            return replaced;
        }

        private static void CopyChangedRows(Buffer src, WriteableBitmap wb)
        {
            int stride = src.W * 4; // 32 bpp rows are always DWORD aligned, in the DIB and in the Bgr32 bitmap
            for (int top = 0; top < src.H; top += ChunkRows)
            {
                int rows = Math.Min(ChunkRows, src.H - top);
                var a = new ReadOnlySpan<byte>((byte*)src.Bits + top * stride, rows * stride);
                var b = new Span<byte>((byte*)wb.BackBuffer + top * stride, rows * stride);
                if (a.SequenceEqual(b)) continue;
                a.CopyTo(b);
                wb.AddDirtyRect(new Int32Rect(0, top, src.W, rows));
            }
        }

        private void Run()
        {
            while (!_stop)
            {
                if (!_enabled) { _wake.WaitOne(); continue; }
                long t0 = Stopwatch.GetTimestamp();
                IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
                try
                {
                    for (int s = 0; s < 2 && !_stop; s++) CaptureSide(_sides[s], screenDc);
                }
                catch (Exception ex) { Debug.WriteLine("Screen capture failed: " + ex.Message); }
                finally { NativeMethods.ReleaseDC(IntPtr.Zero, screenDc); }

                double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                LastMs = ms;
                int wait = _intervalMs - (int)ms;
                if (wait > 0) _wake.WaitOne(wait);
            }
            lock (_lock) foreach (var side in _sides) foreach (var b in side.Buffers) Free(b);
        }

        private void CaptureSide(Side side, IntPtr screenDc)
        {
            System.Drawing.Rectangle r;
            Buffer back;
            lock (_lock)
            {
                r = side.Request;
                back = side.Buffers[side.Back];
                if (r.Width <= 0 || r.Height <= 0) return;
                // Reallocating touches only the back buffer; the UI reads the front one.
                if ((back.W != r.Width || back.H != r.Height) && !Allocate(back, r.Width, r.Height)) return;
            }

            bool ok = NativeMethods.BitBlt(back.Dc, 0, 0, r.Width, r.Height, screenDc, r.X, r.Y, NativeMethods.NOTSRCCOPY);
            NativeMethods.GdiFlush(); // the bits must be complete before the UI thread reads them
            if (!ok) return;

            lock (_lock)
            {
                side.Back = 1 - side.Back;
                side.Ready = true;
            }
        }

        /// <summary>New DIB of w x h; on failure the buffer stays empty and nothing is published.</summary>
        private static bool Allocate(Buffer b, int w, int h)
        {
            Free(b);
            var bmi = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(NativeMethods.BITMAPINFOHEADER),
                biWidth = w,
                biHeight = -h, // top-down rows, same order as the WPF bitmap
                biPlanes = 1,
                biBitCount = 32,
            };
            IntPtr dc = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) return false;
            IntPtr dib = NativeMethods.CreateDIBSection(dc, ref bmi, NativeMethods.DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero)
            {
                if (dib != IntPtr.Zero) NativeMethods.DeleteObject(dib);
                NativeMethods.DeleteDC(dc);
                return false;
            }
            b.Dc = dc;
            b.Dib = dib;
            b.Bits = bits;
            b.OldBitmap = NativeMethods.SelectObject(dc, dib);
            b.W = w; b.H = h;
            return true;
        }

        private static void Free(Buffer b)
        {
            if (b.Dc == IntPtr.Zero) return;
            NativeMethods.SelectObject(b.Dc, b.OldBitmap);
            NativeMethods.DeleteObject(b.Dib);
            NativeMethods.DeleteDC(b.Dc);
            b.Dc = b.Dib = b.Bits = IntPtr.Zero;
            b.W = b.H = 0;
        }

        public void Dispose()
        {
            _stop = true;
            _wake.Set();
            _thread.Join(500);
        }
    }
}
