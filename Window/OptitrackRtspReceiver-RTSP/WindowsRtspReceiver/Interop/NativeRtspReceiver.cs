using System.Runtime.InteropServices;
using System.Text;

namespace WindowsRtspReceiver.Interop;

internal sealed class NativeRtspReceiver : IDisposable
{
    private const string LibraryName = "OptitrackRtspNative.dll";
    private readonly object _sync = new();
    private IntPtr _handle;
    private IntPtr _frameBuffer;
    private int _frameBufferCapacity;
    private byte[] _managedFrameBuffer = Array.Empty<byte>();
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

    public bool TryGetFrame(out byte[] pixels, out int width, out int height, out int stride, out ulong sequence)
    {
        pixels = Array.Empty<byte>();
        width = 0;
        height = 0;
        stride = 0;
        sequence = 0;
        long timestampMs = 0;
        var requiredBytes = 0;

        lock (_sync)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (NativeMethods.GetFrame(
                        _handle,
                        IntPtr.Zero,
                        0,
                        out width,
                        out height,
                        out stride,
                        out sequence,
                        out timestampMs,
                        out requiredBytes) == 0 ||
                    requiredBytes <= 0 ||
                    width <= 0 ||
                    height <= 0 ||
                    stride < (long)width * 4 ||
                    requiredBytes != (long)stride * height ||
                    requiredBytes > 256 * 1024 * 1024)
                {
                    return false;
                }
                if (sequence == _lastFrameSequence)
                {
                    return false;
                }

                EnsureFrameBuffer(requiredBytes);
                var result = NativeMethods.GetFrame(
                    _handle,
                    _frameBuffer,
                    _frameBufferCapacity,
                    out width,
                    out height,
                    out stride,
                    out sequence,
                    out timestampMs,
                    out requiredBytes);
                if (result == 1 && requiredBytes <= _frameBufferCapacity)
                {
                    if (_managedFrameBuffer.Length != requiredBytes)
                    {
                        _managedFrameBuffer = new byte[requiredBytes];
                    }

                    Marshal.Copy(_frameBuffer, _managedFrameBuffer, 0, requiredBytes);
                    pixels = _managedFrameBuffer;
                    _lastFrameSequence = sequence;
                    return true;
                }
            }

            return false;
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

            if (_frameBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_frameBuffer);
                _frameBuffer = IntPtr.Zero;
                _frameBufferCapacity = 0;
            }
        }
    }

    private void EnsureFrameBuffer(int requiredBytes)
    {
        if (requiredBytes <= _frameBufferCapacity)
        {
            return;
        }

        var replacement = Marshal.AllocHGlobal(requiredBytes);
        if (_frameBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_frameBuffer);
        }

        _frameBuffer = replacement;
        _frameBufferCapacity = requiredBytes;
    }

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

        [DllImport(LibraryName, EntryPoint = "optitrack_rtsp_get_last_error", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void GetLastError(IntPtr handle, StringBuilder error, int errorCapacity);
    }
}
