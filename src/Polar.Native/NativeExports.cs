using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace PolarBle.Native
{
    /// <summary>Versioned C ABI. No managed objects or exceptions cross the boundary.</summary>
    public static unsafe class NativeExports
    {
        private static readonly ConcurrentDictionary<long, NativeContext> Contexts = new();
        private static long _nextContext;
        [ThreadStatic] private static string? _lastError;

        [UnmanagedCallersOnly(EntryPoint = "polar_abi_version", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static int AbiVersion() => 1;

        [UnmanagedCallersOnly(EntryPoint = "polar_create", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static long Create()
        {
            try
            {
                var context = new NativeContext();
                var handle = Interlocked.Increment(ref _nextContext);
                Contexts[handle] = context;
                return handle;
            }
            catch (Exception ex) { _lastError = ex.ToString(); return 0; }
        }

        [UnmanagedCallersOnly(EntryPoint = "polar_destroy", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static int Destroy(long handle)
        {
            try { if (Contexts.TryRemove(handle, out var context)) context.Dispose(); return 0; }
            catch (Exception ex) { _lastError = ex.ToString(); return -1; }
        }

        [UnmanagedCallersOnly(EntryPoint = "polar_submit", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static int Submit(long handle, long requestId, int operation, long connectionId, byte* data, int length)
        {
            try
            {
                if (length < 0 || length > Wire.MaxPayload || (length > 0 && data == null)) throw new ArgumentOutOfRangeException(nameof(length));
                Context(handle).Submit(requestId, operation, connectionId, new ReadOnlySpan<byte>(data, length).ToArray());
                return 0;
            }
            catch (Exception ex) { _lastError = ex.ToString(); return -1; }
        }

        [UnmanagedCallersOnly(EntryPoint = "polar_cancel", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static int Cancel(long handle, long requestId)
        {
            try { return Context(handle).Cancel(requestId) ? 1 : 0; }
            catch (Exception ex) { _lastError = ex.ToString(); return -1; }
        }

        [UnmanagedCallersOnly(EntryPoint = "polar_max_write_size", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static int MaxWriteSize(long handle, long connectionId)
        {
            try { return Context(handle).MaxWriteSize(connectionId); }
            catch (Exception ex) { _lastError = ex.ToString(); return -1; }
        }

        [UnmanagedCallersOnly(EntryPoint = "polar_poll", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static int Poll(long handle, int* kind, long* requestId, long* connectionId, byte* buffer, int capacity, int* required)
        {
            try
            {
                if (kind == null || requestId == null || connectionId == null || required == null || capacity < 0)
                    throw new ArgumentException("Invalid event output pointer or buffer capacity.");
                var context = Context(handle);
                var item = context.Peek();
                if (item == null) { *required = 0; *kind = 0; *requestId = 0; *connectionId = 0; return 0; }
                *kind = item.Kind; *requestId = item.RequestId; *connectionId = item.ConnectionId; *required = item.Payload.Length;
                if (capacity < item.Payload.Length || (item.Payload.Length != 0 && buffer == null)) return 2;
                item.Payload.AsSpan().CopyTo(new Span<byte>(buffer, capacity));
                context.Dequeue(item);
                return 1;
            }
            catch (Exception ex) { _lastError = ex.ToString(); return -1; }
        }

        [UnmanagedCallersOnly(EntryPoint = "polar_last_error", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static int LastError(byte* buffer, int capacity)
        {
            try
            {
                var bytes = Wire.Utf8.GetBytes(_lastError ?? "Unknown native BLE error.");
                if (buffer != null && capacity >= bytes.Length) bytes.AsSpan().CopyTo(new Span<byte>(buffer, capacity));
                return bytes.Length; // Byte count, no trailing NUL. Call on the same native thread.
            }
            catch { return -1; }
        }

        private static NativeContext Context(long handle)
            => Contexts.TryGetValue(handle, out var context) ? context : throw new ObjectDisposedException("Native BLE context");
    }
}
