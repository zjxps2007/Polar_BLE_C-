// Adapted from Polar BLE SDK RFC60/RFC76 implementation. See THIRD-PARTY-NOTICES.
using System;
using System.Collections.Generic;
using System.IO;

namespace PolarBle.Protocol.Pftp
{
    public sealed class PftpResponseException : PolarException
    {
        public ushort ErrorCode { get; }
        public PftpResponseException(ushort code) : base("Polar file transfer error " + code) { ErrorCode = code; }
    }

    public static class PftpFraming
    {
        public static byte[] Request(byte[] operation, byte[]? data = null)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            if (operation.Length > 0x7fff) throw new ArgumentOutOfRangeException(nameof(operation));
            var result = new byte[checked(2 + operation.Length + (data?.Length ?? 0))];
            result[0] = (byte)operation.Length; result[1] = (byte)(operation.Length >> 8);
            Buffer.BlockCopy(operation, 0, result, 2, operation.Length);
            if (data != null) Buffer.BlockCopy(data, 0, result, operation.Length + 2, data.Length);
            return result;
        }

        public static byte[] Query(ushort id, byte[]? parameters = null)
        {
            if (id > 0x7fff) throw new ArgumentOutOfRangeException(nameof(id));
            var result = new byte[2 + (parameters?.Length ?? 0)];
            result[0] = (byte)id; result[1] = (byte)((id >> 8) | 0x80);
            if (parameters != null) Buffer.BlockCopy(parameters, 0, result, 2, parameters.Length);
            return result;
        }

        public static byte[] Notification(byte id, byte[]? parameters = null)
        {
            var result = new byte[1 + (parameters?.Length ?? 0)]; result[0] = id;
            if (parameters != null) Buffer.BlockCopy(parameters, 0, result, 1, parameters.Length);
            return result;
        }

        /// <param name="maxPayload">Maximum characteristic value length, excluding ATT headers.</param>
        public static IEnumerable<byte[]> Fragment(byte[] message, int maxPayload)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            if (maxPayload < 2 || maxPayload > 512) throw new ArgumentOutOfRangeException(nameof(maxPayload));
            int offset = 0, sequence = 0;
            do
            {
                int size = Math.Min(maxPayload - 1, message.Length - offset);
                bool last = offset + size == message.Length;
                var packet = new byte[size + 1];
                packet[0] = (byte)((sequence << 4) | (offset == 0 ? 0 : 1) | (last ? 2 : 6));
                Buffer.BlockCopy(message, offset, packet, 1, size);
                offset += size; sequence = (sequence + 1) & 15;
                yield return packet;
            } while (offset < message.Length);
        }
    }

    /// <summary>Strict RFC76 sequence validation, bounded response accumulation, and 16-bit errors.</summary>
    public sealed class PftpAssembler : IDisposable
    {
        private readonly MemoryStream _data = new MemoryStream();
        private readonly int _maxBytes;
        private int _sequence;
        private bool _started, _completed;
        public PftpAssembler(int maxBytes = 64 * 1024 * 1024)
        { if (maxBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxBytes)); _maxBytes = maxBytes; }
        public long Length => _data.Length;
        public bool Add(byte[] packet)
        {
            if (_completed) throw new PolarProtocolException("RFC76 response is already complete.");
            if (packet == null || packet.Length == 0) throw new PolarProtocolException("Empty RFC76 packet.");
            int status = (packet[0] >> 1) & 3;
            if ((packet[0] >> 4) != _sequence || (packet[0] & 1) != (_started ? 1 : 0))
                throw new PolarProtocolException("RFC76 packet lost or response out of sequence.");
            if (status == 2) throw new PolarProtocolException("Reserved RFC76 status.");
            _started = true; _sequence = (_sequence + 1) & 15;
            if (status == 0)
            {
                if (packet.Length != 3) throw new PolarProtocolException("Invalid RFC76 status response length.");
                ushort error = (ushort)(packet[1] | (packet[2] << 8));
                if (error != 0) throw new PftpResponseException(error);
                _completed = true; return true;
            }
            if (_data.Length + packet.Length - 1 > _maxBytes) throw new PolarProtocolException("RFC76 response exceeds configured limit.");
            _data.Write(packet, 1, packet.Length - 1);
            _completed = status == 1;
            return _completed;
        }
        public byte[] GetResult()
        { if (!_completed) throw new InvalidOperationException("Response is incomplete."); return _data.ToArray(); }
        public void Dispose() => _data.Dispose();
    }
}
