// Ported from Polar BLE SDK. Copyright Polar Electro Oy. See LICENSES/Polar_SDK_License.txt.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using PolarBle.Protocol.Pmd;

namespace PolarBle.Offline
{
    public sealed class OfflineRecording
    {
        public uint Version { get; }
        public uint FirmwareHash { get; }
        public uint HeaderFree { get; }
        public DateTime StartTimeUtc { get; }
        public PmdSettings? Settings { get; }
        public IReadOnlyList<PmdDataBatch> Batches { get; }
        public ulong LastTimestampNanoseconds => Batches.Count == 0 ? 0 : Batches[Batches.Count - 1].FrameTimestampNanoseconds;
        internal OfflineRecording(uint version, uint hash, uint free, DateTime start, PmdSettings? settings, IReadOnlyList<PmdDataBatch> batches)
        { Version = version; FirmwareHash = hash; HeaderFree = free; StartTimeUtc = start; Settings = settings; Batches = batches; }
    }
    public static class OfflineRecordingParser
    {
        public static OfflineRecording Parse(byte[] file, PmdSecret? secret = null,
            ulong previousTimestamp = 0, IReadOnlyCollection<int>? derivedMethods = null)
        {
            PmdBinary.Require(file, 0, 1);
            var headerStrategy = (PmdSecurityStrategy)file[0];
            if (headerStrategy > PmdSecurityStrategy.Aes256) throw new PolarProtocolException("Unknown offline security strategy.");
            if (headerStrategy != PmdSecurityStrategy.None && secret == null) throw new PolarProtocolException("Offline recording key is required.");
            if (headerStrategy >= PmdSecurityStrategy.Aes128 && secret!.Strategy != headerStrategy)
                throw new PolarProtocolException("Offline recording key strategy does not match the file.");
            var metadataBytes = PmdBinary.Slice(file, 1, headerStrategy >= PmdSecurityStrategy.Aes128 ? (file.Length - 1) / 16 * 16 : file.Length - 1);
            if (headerStrategy != PmdSecurityStrategy.None) metadataBytes = secret!.Decrypt(metadataBytes);
            PmdBinary.Require(metadataBytes, 0, 37);
            if (PmdBinary.Unsigned(metadataBytes, 0, 4) != 0x3D7C4C2B) throw new PolarProtocolException("Offline recording signature mismatch; the file or key is invalid.");
            uint version = (uint)PmdBinary.Unsigned(metadataBytes, 4, 4), hash = (uint)PmdBinary.Unsigned(metadataBytes, 12, 4);
            uint free = (uint)PmdBinary.Unsigned(metadataBytes, 8, 4);
            var dateText = Encoding.ASCII.GetString(metadataBytes, 16, 19);
            if (!DateTime.TryParseExact(dateText, new[] { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss" }, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var start))
                throw new PolarProtocolException("Invalid offline recording start time.");
            int offset = 36, settingsLength = metadataBytes[offset++];
            var settings = settingsLength == 0 ? null : PmdSettings.Parse(PmdBinary.Slice(metadataBytes, offset, settingsLength)); offset += settingsLength;
            PmdBinary.Require(metadataBytes, offset, 1); int securityLength = metadataBytes[offset++];
            PmdSecret payloadSecret;
            if (securityLength == 0) payloadSecret = secret ?? new PmdSecret(PmdSecurityStrategy.None, Array.Empty<byte>());
            else
            {
                PmdBinary.Require(metadataBytes, offset, securityLength); var strategy = (PmdSecurityStrategy)metadataBytes[offset];
                if (strategy == PmdSecurityStrategy.None) payloadSecret = new PmdSecret(strategy, Array.Empty<byte>());
                else if (strategy == PmdSecurityStrategy.Xor)
                {
                    if (securityLength < 2 || secret == null) throw new PolarProtocolException("Missing offline payload XOR key.");
                    var key = secret.ExportKey(); int index = metadataBytes[offset + 1];
                    if (index >= key.Length) throw new PolarProtocolException("Offline XOR key index exceeds key length.");
                    payloadSecret = new PmdSecret(strategy, new[] { key[index] });
                }
                else payloadSecret = new PmdSecret(strategy, secret?.ExportKey() ?? throw new PolarProtocolException("Missing offline payload key."));
                offset += securityLength;
            }
            if (headerStrategy >= PmdSecurityStrategy.Aes128) offset += 16 - offset % 16;
            if (offset == metadataBytes.Length && file.Length == offset + 1)
                return new OfflineRecording(version, hash, free, start, settings, Array.Empty<PmdDataBatch>());
            int packetSize = (int)PmdBinary.Unsigned(metadataBytes, offset, 2); offset += 2;
            if (headerStrategy >= PmdSecurityStrategy.Aes128) offset += 16 - offset % 16;
            var data = payloadSecret.Decrypt(PmdBinary.Slice(file, 1 + offset, file.Length - 1 - offset));
            var batches = new List<PmdDataBatch>(); offset = 0;
            int rate = settings?.Get(PmdSettingType.SampleRate) ?? 0;
            float factor = settings == null ? 1 : PmdBinary.Float(settings.Get(PmdSettingType.Factor, BitConverter.SingleToInt32Bits(1f)));
            if (settings != null && settings.Available.TryGetValue(PmdSettingType.DerivedMeasurementMethod, out var methods)) derivedMethods = methods.ToArray();
            while (offset < data.Length)
            {
                // AES files can finish with zero padding after a zero packet-length terminator.
                if (packetSize == 0)
                {
                    if (data.Skip(offset).Any(b => b != 0)) throw new PolarProtocolException("Unexpected data after offline terminator.");
                    break;
                }
                if (packetSize < 10) throw new PolarProtocolException("Invalid offline PMD packet size.");
                var frame = PmdBinary.Slice(data, offset, packetSize); offset += packetSize;
                var batch = PmdFrameParser.Parse(frame, rate, factor, previousTimestamp, derivedMethods); batches.Add(batch);
                previousTimestamp = batch.FrameTimestampNanoseconds;
                if (offset == data.Length) break;
                packetSize = (int)PmdBinary.Unsigned(data, offset, 2); offset += 2;
            }
            return new OfflineRecording(version, hash, free, start, settings, batches);
        }
    }
}
