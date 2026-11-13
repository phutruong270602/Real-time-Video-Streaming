using System.Runtime.InteropServices;
using System.Text;

namespace WindowsRtspReceiver.Interop;

internal sealed class NativeRtspReceiver : IDisposable
{
    private const string LibraryName = "OptitrackRtspNative.dll";
    private readonly object _sync = new();
    private IntPtr _handle;
    private ulong _lastFrameSequence;

    public NativeRtspReceiver()
    {
        _handle = NativeMethods.Create();
        if (_handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Could not create the native RTSP receiver.");
        }
    }

    public bool Start(
        string url,
        bool useTcp,
        int timeoutMs,
        bool reconnect,
        int reconnectDelayMs,
        int queueCapacity,
        out string error)
    {
        lock (_sync)
        {
            var errorBuffer = new StringBuilder(2048);
            var started = NativeMethods.StartEx(
                _handle,
                url,
                useTcp ? 1 : 0,
                timeoutMs,
                reconnect ? 1 : 0,
                reconnectDelayMs,
                queueCapacity,
                errorBuffer,
                errorBuffer.Capacity) != 0;
            error = errorBuffer.ToString();
            if (started)
            {
                _lastFrameSequence = 0;
            }
            return started;
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            NativeMethods.Stop(_handle);
        }
    }

    public bool TryGetStats(out NativeStats stats)
    {
        lock (_sync)
        {
            return NativeMethods.GetStats(_handle, out stats) != 0;
        }
    }

    public string GetLastError()
    {
        lock (_sync)
        {
            var errorBuffer = new StringBuilder(2048);
            NativeMethods.GetLastError(_handle, errorBuffer, errorBuffer.Capacity);
            return errorBuffer.ToString();
        }
    }

    public bool TryGetFrameInfo(
        out int width,
        out int height,
        out int stride,
        out ulong sequence,
        out int requiredBytes)
    {
        width = 0;
        height = 0;
        stride = 0;
        sequence = 0;
        long timestampMs = 0;

        lock (_sync)
        {
            var result = NativeMethods.GetFrame(
                _handle,
                IntPtr.Zero,
                0,
                out width,
                out height,
                out stride,
                out sequence,
                out timestampMs,
                out requiredBytes);
            return result == 2 && IsValidFrame(width, height, stride, requiredBytes);
        }
    }

    public bool TryCopyFrameTo(
        IntPtr destination,
        int destinationCapacity,
        int expectedWidth,
        int expectedHeight,
        int expectedStride,
        out ulong sequence)
    {
        sequence = 0;
        if (destination == IntPtr.Zero || destinationCapacity <= 0)
        {
            return false;
        }

        lock (_sync)
        {
            if (NativeMethods.GetFrame(
                    _handle,
                    IntPtr.Zero,
                    0,
                    out var width,
                    out var height,
                    out var stride,
                    out var currentSequence,
                    out var timestampMs,
                    out var requiredBytes) != 2 ||
                !IsValidFrame(width, height, stride, requiredBytes) ||
                width != expectedWidth ||
                height != expectedHeight ||
                stride != expectedStride ||
                requiredBytes > destinationCapacity ||
                currentSequence == _lastFrameSequence)
            {
                return false;
            }

            var result = NativeMethods.GetFrame(
                _handle,
                destination,
                destinationCapacity,
                out width,
                out height,
                out stride,
                out sequence,
                out timestampMs,
                out requiredBytes);
            if (result != 1 ||
                width != expectedWidth ||
                height != expectedHeight ||
                stride != expectedStride ||
                requiredBytes > destinationCapacity)
            {
                return false;
            }

            _lastFrameSequence = sequence;
            return true;
        }
    }

    public bool TryDequeueReviewFrame(
        out byte[] jpegBytes,
        out int width,
        out int height,
        out ulong sequence,
        out long timestampMs)
    {
        jpegBytes = [];
        width = 0;
        height = 0;
        sequence = 0;
        timestampMs = 0;

        lock (_sync)
        {
            var result = NativeMethods.DequeueReviewFrame(
                _handle,
                null,
                0,
                out width,
                out height,
                out sequence,
                out timestampMs,
                out var requiredBytes);
            if (result != 2 || requiredBytes <= 0 || requiredBytes > 256 * 1024 * 1024)
            {
                return false;
            }

            jpegBytes = new byte[requiredBytes];
            result = NativeMethods.DequeueReviewFrame(
                _handle,
                jpegBytes,
                jpegBytes.Length,
                out width,
                out height,
                out sequence,
                out timestampMs,
                out requiredBytes);
            if (result == 1 && requiredBytes == jpegBytes.Length)
            {
                return true;
            }

            jpegBytes = [];
            return false;
        }
    }

    public bool SetReviewMode(bool enabled)
    {
        lock (_sync)
        {
            return _handle != IntPtr.Zero &&
                NativeMethods.SetReviewMode(_handle, enabled ? 1 : 0) != 0;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_handle != IntPtr.Zero)
            {
                NativeMethods.Destroy(_handle);
                _handle = IntPtr.Zero;
            }
        }
    }

    private static bool IsValidFrame(int width, int height, int stride, int requiredBytes) =>
        requiredBytes > 0 &&
        width > 0 &&
        height > 0 &&
        stride >= (long)width * 4 &&
        requiredBytes == (long)stride * height &&
        requiredBytes <= 256 * 1024 * 1024;

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeStats
    {
        public int Running;
        public int Connected;
        public int Width;
        public int Height;
        public ulong PacketsReceived;
        public ulong BytesReceived;
        public ulong PacketsDropped;
        public ulong FramesDecoded;
    }

    private static class NativeMethods
    {
        [DllImport(LibraryName, EntryPoint = "optitrack_rtsp_create", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr Create();

        [DllImport(LibraryName, EntryPoint = "optitrack_rtsp_destroy", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Destroy(IntPtr handle);

        [DllImport(LibraryName, EntryPoint = "optitrack_rtsp_start_ex", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int StartEx(
            IntPtr handle,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string url,
            int useTcp,
            int timeoutMs,
            int reconnect,
            int reconnectDelayMs,
            int queueCapacity,
            StringBuilder error,
            int errorCapacity);

        [DllImport(LibraryName, EntryPoint = "optitrack_rtsp_stop", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Stop(IntPtr handle);

        [DllImport(LibraryName, EntryPoint = "optitrack_rtsp_get_stats", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int GetStats(IntPtr handle, out NativeStats stats);

        [DllImport(LibraryName, EntryPoint = "optitrack_rtsp_get_frame", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int GetFrame(
            IntPtr handle,
            IntPtr destination,
            int destinationCapacity,
            out int width,
            out int height,
            out int stride,
            out ulong sequence,
            out long timestampMs,
            out int requiredBytes);

        [DllImport(LibraryName, EntryPoint = "optitrack_rtsp_dequeue_review_frame", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int DequeueReviewFrame(
            IntPtr handle,
            [Out] byte[]? destination,
            int destinationCapacity,
            out int width,
            out int height,
            out ulong sequence,
            out long timestampMs,
            out int requiredBytes);

        [DllImport(LibraryName, EntryPoint = "optitrack_rtsp_set_review_mode", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SetReviewMode(IntPtr handle, int enabled);

        [DllImport(LibraryName, EntryPoint = "optitrack_rtsp_get_last_error", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void GetLastError(IntPtr handle, StringBuilder error, int errorCapacity);
    }
}
