using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PolarBle;
using PolarBle.Offline;
using PolarBle.Protocol.HeartRate;
using PolarBle.Protocol.Pmd;
using PolarBle.Transport;

namespace Polar.Tests
{
    internal static class PmdTests
    {
        public static void Run()
        {
            Settings(); DeltaGoldenVectors(); RawSignals(); CompressedSignals(); Derived(); HeartRate(); Offline(); Crypto(); EncryptedOfflineGolden();
            ControlPoint().GetAwaiter().GetResult();
        }
        private static byte[] Hex(string text) => text.Split(new[] { ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => Convert.ToByte(x, 16)).ToArray();
        private static byte[] Frame(PmdMeasurementType type, byte format, byte[] data, ulong timestamp = 2_000_000_000)
        { return new[] { (byte)type }.Concat(BitConverter.GetBytes(timestamp)).Concat(new[] { format }).Concat(data).ToArray(); }
        private static void Settings()
        {
            var available = PmdSettings.Parse(Hex("00 02 34 00 68 00 01 01 10 00 04 01 03"));
            Assert.Equal(104, available.SelectMaximum().Get(PmdSettingType.SampleRate));
            Assert.Equal(3, available.Get(PmdSettingType.Channels));
            var selected = new PmdSettings(new Dictionary<PmdSettingType, int> { [PmdSettingType.SampleRate] = 52, [PmdSettingType.DerivedMeasurementMethod] = 3, [PmdSettingType.Factor] = BitConverter.SingleToInt32Bits(1f) });
            Assert.SequenceEqual(Hex("00 01 34 00 07 02 00 01"), selected.Serialize());
            Assert.Equal(1.0f, BitConverter.Int32BitsToSingle(selected.Get(PmdSettingType.Factor)));
            Assert.Throws<PolarProtocolException>(() => PmdSettings.Parse(Hex("00 02 01")));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PmdSettings(new Dictionary<PmdSettingType, int> { [PmdSettingType.Channels] = 256 }));
            var triggers = PmdOfflineTrigger.Parse(Hex("01 01 02 04 00 01 34 00 00 03"));
            Assert.Equal(PmdTriggerMode.SystemStart, triggers.Mode);
            Assert.Equal(52, triggers.Triggers[PmdMeasurementType.Accelerometer]!.Get(PmdSettingType.SampleRate));
            Assert.True(triggers.Triggers[PmdMeasurementType.Ppi] == null);
        }
        private static void DeltaGoldenVectors()
        {
            // Upstream BlePmdClientParsersTest: signed reference values and 32-bit IEEE-754 delta vector.
            Assert.SequenceEqual(new[] { -1, 0, 32767, -32768 }, PmdFrameParser.DecodeDelta(Hex("FF FF 00 00 FF 7F 00 80"), 4, 16)[0]);
            byte[] vector = Hex("00 00 80 3F 00 00 20 41 00 00 A0 41 1C 06 " +
                "00 00 A0 01 00 00 08 CD CC EC 0D 00 00 08 CD CC EC 3D 33 33 1A CD CC EC 3D 33 33 1A 00 00 C0 30 33 33 1A 00 00 C0 A0 99 99 DA 00 00 C0 A0 99 99 DA 66 66 A6 A1 99 99 DA 66 66 A6 01 00 00 0E " +
                "20 03 66 66 A6 01 00 00 E0 00 42 6C 45 0E 00 00 E0 00 42 6C 45 0E 2B F8 ED 21 00 00 60 FD BE 93 BA F0 93 9B 4C CF");
            var values = PmdFrameParser.DecodeDelta(vector, 3, 32, false);
            Assert.Equal(10, values.Count);
            Assert.SequenceEqual(new[] { 0x3f800000, 0x41200000, 0x41a00000 }, values[0]);
            Assert.SequenceEqual(new[] { 0x41200000, 0x41a00000, unchecked(0x41a00000 + (int)0xfdeccccd) }, values[1]);
            // Signed 4-bit deltas +1, -1, -8, crossing byte boundaries for three channels.
            var simple = PmdFrameParser.DecodeDelta(Hex("01 00 02 00 03 00 04 01 F1 08"), 3, 16);
            Assert.SequenceEqual(new[] { 2, 1, -5 }, simple[1]);
            Assert.Throws<PolarProtocolException>(() => PmdFrameParser.DecodeDelta(Hex("01 00 08 02 01"), 1, 16));
            Assert.Throws<PolarProtocolException>(() => PmdFrameParser.DecodeDelta(Hex("01 00 21 01 01 00 00 00 00"), 1, 16));
            var times = PmdFrameParser.Timestamps(700_000_000_000_000_000, 700_000_000_000_000_003, 3, 0);
            Assert.SequenceEqual(new ulong[] { 700_000_000_000_000_001, 700_000_000_000_000_002, 700_000_000_000_000_003 }, times);
            Assert.Throws<PolarProtocolException>(() => PmdFrameParser.Timestamps(20, 19, 1, 0));
        }
        private static void RawSignals()
        {
            // First two samples of upstream AccDataTest raw type 1 golden vector.
            var acc = PmdFrameParser.Parse(Frame(PmdMeasurementType.Accelerometer, 1, Hex("F7 FF FF FF E7 03 F8 FF FE FF E5 03")), 52);
            Assert.SequenceEqual(new double[] { -9, -1, 999 }, acc.Samples[0].Values);
            Assert.SequenceEqual(new double[] { -8, -2, 997 }, acc.Samples[1].Values);
            Assert.Equal(2_000_000_000UL, acc.Samples[1].TimestampNanoseconds);
            var ecg = PmdFrameParser.Parse(Frame(PmdMeasurementType.Ecg, 0, Hex("FF FF FF 00 00 80 FF FF 7F")), 130);
            Assert.SequenceEqual(new double[] { -1, -8388608, 8388607 }, ecg.Samples.Select(s => s.Values[0]));
            for (byte format = 1; format <= 3; format++)
                Assert.Equal(1, PmdFrameParser.Parse(Frame(PmdMeasurementType.Ecg, format, new byte[format == 3 ? 7 : 3]), 130).Samples.Count);
            var ppi = PmdFrameParser.Parse(Frame(PmdMeasurementType.Ppi, 0, Hex("3C E8 03 05 00 06 3D E8 03 06 00 01")));
            Assert.Equal(1_000_000_000UL, ppi.Samples[0].TimestampNanoseconds);
            Assert.Equal(2_000_000_000UL, ppi.Samples[1].TimestampNanoseconds);
            Assert.True((bool)ppi.Samples[1].Metadata["blocker"]);
            foreach (var type in new[] { PmdMeasurementType.Temperature, PmdMeasurementType.SkinTemperature, PmdMeasurementType.Pressure })
                Assert.Equal(20.5, PmdFrameParser.Parse(Frame(type, 0, BitConverter.GetBytes(20.5f)), 4).Samples[0].Values[0]);
            Assert.Equal(3, PmdFrameParser.Parse(Frame(PmdMeasurementType.OfflineHeartRate, 0, Hex("3C 3D 3E"))).Samples.Count);
            Assert.SequenceEqual(new double[] { 60, 5, 61 }, PmdFrameParser.Parse(Frame(PmdMeasurementType.OfflineHeartRate, 1, Hex("3C 05 3D"))).Samples[0].Values);
            foreach (byte format in new byte[] { 0, 4, 5, 6, 9, 14 })
            {
                int length = format == 0 ? 12 : format == 4 || format == 9 ? 36 : format == 5 ? 4 : format == 6 ? 8 : 3;
                Assert.Equal(1, PmdFrameParser.Parse(Frame(PmdMeasurementType.Ppg, format, new byte[length]), 52).Samples.Count);
            }
            foreach (byte format in new byte[] { 0, 1, 2 })
                Assert.Equal(1, PmdFrameParser.Parse(Frame(PmdMeasurementType.Location, format, new byte[format == 0 ? 51 : format == 1 ? 6 : 41]), 1).Samples.Count);
            var nmea = PmdFrameParser.Parse(Frame(PmdMeasurementType.Location, 3, Hex("01 00 00 00 03 00 01 41 42 43")));
            Assert.Equal("ABC", (string)nmea.Samples[0].Metadata["nmea"]);
            Assert.Throws<PolarProtocolException>(() => PmdFrameParser.Parse(Frame(PmdMeasurementType.Ecg, 0, Hex("01 02")), 130));
            Assert.Throws<PolarProtocolException>(() => PmdFrameParser.Parse(Frame(PmdMeasurementType.Gyroscope, 0, new byte[6]), 52));
        }
        private static void CompressedSignals()
        {
            foreach (byte format in new byte[] { 0, 1 })
            {
                var acc = PmdFrameParser.Parse(Frame(PmdMeasurementType.Accelerometer, (byte)(128 | format), Hex("01 00 FE FF 03 00")), 52, 1);
                Assert.Equal(format == 0 ? 1000.0 : 1.0, acc.Samples[0].Values[0]);
                var gyro = PmdFrameParser.Parse(Frame(PmdMeasurementType.Gyroscope, (byte)(128 | format), format == 0 ? Hex("01 00 02 00 03 00") : Enumerable.Repeat(BitConverter.GetBytes(2f), 3).SelectMany(x => x).ToArray()), 52);
                Assert.Equal(format == 0 ? 1.0 : 2.0, gyro.Samples[0].Values[0]);
                var mag = PmdFrameParser.Parse(Frame(PmdMeasurementType.Magnetometer, (byte)(128 | format), format == 0 ? Hex("01 00 02 00 03 00") : Hex("E8 03 D0 07 B8 0B 03 00")), 52);
                Assert.Equal(1.0, mag.Samples[0].Values[0]);
            }
            foreach (var type in new[] { PmdMeasurementType.Temperature, PmdMeasurementType.SkinTemperature, PmdMeasurementType.Pressure })
                Assert.Equal(20.5, PmdFrameParser.Parse(Frame(type, 128, BitConverter.GetBytes(20.5f)), 4).Samples[0].Values[0]);
            foreach (byte format in new byte[] { 0, 7, 8, 10, 13 })
            {
                int channels = format == 0 ? 4 : format == 7 ? 17 : format == 8 ? 25 : format == 10 ? 21 : 3;
                var ppg = PmdFrameParser.Parse(Frame(PmdMeasurementType.Ppg, (byte)(format | 128), new byte[channels * 3]), 52);
                Assert.Equal(format == 0 ? channels : channels - 1, ppg.Samples[0].Values.Count);
            }
        }
        private static void Derived()
        {
            var batch = PmdFrameParser.Parse(Frame(PmdMeasurementType.Accelerometer, 0, Hex("01 00 FE FF 2C 01")), derivedMethods: new[] { 0 });
            Assert.SequenceEqual(new double[] { 1, -2, 300 }, batch.Samples[0].Values);
            batch = PmdFrameParser.Parse(Frame(PmdMeasurementType.Accelerometer, 0, Hex("FF FF")), derivedMethods: new[] { 5 });
            Assert.Equal(65535.0, batch.Samples[0].Values[0]);
            batch = PmdFrameParser.Parse(Frame(PmdMeasurementType.DerivedMeasurement, 0, Hex("00 02 21 00 01 00 FE FF 2C 01 FF FF")));
            Assert.SequenceEqual(new double[] { 1, -2, 300, 65535 }, batch.Samples[0].Values);
        }
        private static void HeartRate()
        {
            var hr = HeartRateParser.Parse(Hex("1F 2C 01 34 12 00 04 00 02"));
            Assert.Equal(300, hr.BeatsPerMinute); Assert.Equal(0x1234, hr.EnergyExpendedKilojoules!.Value);
            Assert.True(hr.SensorContact); Assert.True(hr.SensorContactSupported);
            Assert.SequenceEqual(new[] { 1000, 500 }, hr.RrIntervalsMilliseconds);
            Assert.Throws<PolarProtocolException>(() => HeartRateParser.Parse(Hex("10 3C 00")));
            var adv = PolarHeartRateAdvertisement.Parse(Hex("3F 01 3C 3D"));
            Assert.Equal(7, adv.FrameCounter); Assert.Equal(61, adv.SlowAverageHeartRate); Assert.True(adv.Broadcast);
        }
        private static void Crypto()
        {
            // Exact upstream PmdSecretTest AES golden vector, not generated by this implementation.
            var key = Hex("00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E FF");
            Assert.SequenceEqual(Hex("60 08 6B DA 00 DB 42 62 34 60 27 43 71 A7 53 68"), new PmdSecret(PmdSecurityStrategy.Aes128, key).Decrypt(key));
            Assert.SequenceEqual(Hex("C8 0D 56 BB 97 7A 42 5F 5A A1 CD FC 24 A2 78 12"), new PmdSecret(PmdSecurityStrategy.Aes256, key.Concat(key).ToArray()).Decrypt(key));
            Assert.SequenceEqual(Hex("FE FD"), new PmdSecret(PmdSecurityStrategy.Xor, new byte[] { 255 }).Decrypt(new byte[] { 1, 2 }));
            Assert.Throws<PolarProtocolException>(() => new PmdSecret(PmdSecurityStrategy.Aes128, key).Decrypt(new byte[1]));
            Assert.Throws<ArgumentException>(() => new PmdSecret(PmdSecurityStrategy.Aes256, key));
        }
        private static void Offline()
        {
            // Exact signed-byte vector from upstream OfflineRecordingDataTest.MockTemperatureOfflineRecordingData.
            int[] source = { 0,43,76,124,61,1,0,0,0,0,0,0,0,-89,-40,-1,-78,50,48,50,50,45,48,49,45,48,50,32,49,55,58,50,53,58,50,57,0,11,0,1,4,0,1,1,32,0,4,1,1,0,42,0,12,-101,61,-62,15,109,16,0,0,0,0,48,-93,65,0,48,-93,65,0,32,-93,65,0,32,-93,65,0,48,-93,65,0,48,-93,65,0,48,-93,65,0,32,-93,65 };
            var recording = OfflineRecordingParser.Parse(source.Select(x => unchecked((byte)x)).ToArray());
            Assert.Equal(1u, recording.Version); Assert.Equal(8, recording.Batches.Single().Samples.Count);
            Assert.Equal((double)20.398438f, recording.Batches[0].Samples[0].Values[0]);
            Assert.Equal(new DateTime(2022, 1, 2, 17, 25, 29, DateTimeKind.Utc), recording.StartTimeUtc);
            Assert.Equal(0, OfflineRecordingParser.Parse(source.Take(50).Select(x => unchecked((byte)x)).ToArray()).Batches.Count);
            var damaged = source.Select(x => unchecked((byte)x)).ToArray(); damaged[1] = 0;
            Assert.Throws<PolarProtocolException>(() => OfflineRecordingParser.Parse(damaged));
            Assert.Throws<PolarProtocolException>(() => OfflineRecordingParser.Parse(new byte[] { 2, 0 }));
        }

        private static void EncryptedOfflineGolden()
        {
            // Exact upstream encrypted device recordings; payload uses a different security mode from metadata.
            var secret = new PmdSecret(PmdSecurityStrategy.Aes128, Enumerable.Range(0, 16).Select(i => (byte)i).ToArray());
            var acc = OfflineRecordingParser.Parse(Convert.FromBase64String("AqrHeY93Gc6lYFuFVMXLmnF77SZlsKbz3xhPj4I/QxmeBh5Uk4vR11Wd48Su7zscV7ra+NUpXdYTvmVzomKNtPyENMu13VqHIJfIjFdCWZRRDrJtIjShS38LjTQMXAzQDwofTfwPDxzgyYMID/P/jDwMMuwPzMwP8ewfCL3nMtwPjx0EzbPnjGzkMjwYNggY7g/sz/8ME+0uA0Id9U79CEzhIL7DKgwcAMLyGhq9CjDOE/wx/APy4xty7Bvy4x/I/Pfx3AcxHvA1/Av1DBwxLPBwYxBzzAd0PAwxPCw2HSzwYAMBruPNLPTI8uOPHPQyTPRIA/RzzBtNLPBNIgAKA9TKUuiPQwgJHPTx8x+ODODMIwyz4wtNzQsw7QP3vC/Mw/9KrAwNwgtN0/fzMxQNAwyP0/vNPPQIBh3v8jNCDMEyXRdQ4zANQgYE8/MTjA0KCOzz8v0j/LLk4hcMNvAdSP7M8zrQc4z2Gg0Oi/h2raFLfwuNRAxDDNwPCg387AwEEgDwBfgP8wkP/w3x8g8MBx3+F98T1wn7ASjn/dIVBhfmBeoY4RTr2v0fLfoHGgP6Dg37+g0ID/byDwoN9PDw8wb78PcC/P4FGQv6CAQOC/bzCQ4N/ffwBAb6oQYG/XNMR/UB1GLP+uGbc8zyAPSDM/MMAOwzDQwY7DMM9Qdc8wrhWwwKEru+woauB8K9xzBdHLDNA3Kc98+8940cHE0D+AwDFDH+A3RMBLIS4MhS+EgT9M7sA/EN8PdDELKz80085DE8HEzso4AABAYV+/MMDAcC5uToFS0G+wr3DuINCAIO8AwP8/TxDvEKBYKByAcg2UH24EJT3A2M6E6y9HMBDBkNDgardiGiS38LjTIMXQzuDwoScfP/Skz4MRwEiMznNj0ozQPwcgIcTPMHTPMHDg7wNwwISMzrDB0E9wwMDXzgMvLzyQ30jxzkTtP/iQwMzrMLj+PziAMIBkA98S35oBJOAEISL9MjEwoS8QwEsAMczeP/jHwA95Mrcb0Ls8MHDc0LSOz7Tf0LchzwcgwIMEwACPzTMgz08CwMsjwAjkz4jdP7sgz09zwsspP3jAwIBgwMPO02XuPsPk7yvNBpzAoS8jMAjdPzyUPQTjP0zB3gtwP8Tx38DXz4D0IIT0MIDcMDTd0LMNwD8AwAc/zzTO33chz48iwMNzwEzCP8DEMMztMHyQMIBtEP/OE+Dc3t7xgdTfDmQdYMDqK+saCiS38LjTIMWAzuDwoF8uP/c/wLMzwISPwL9YMHzt33Th0MCAYHQxdNAwIHTenRDQEN/A0KEjMMDPEMAHE8/ExDGDOME7PsCw3zH8zsH7fTA42s5/KMBEnj481D9LMs8DEMCHEsBM0zDE0s+DLsD/MMDHPzDzL8AwoMCDCxXSISICwiHuEsPf7Q2N7oEwMNA87h+/mo37quDgMTUj080P6XPTgy3APq/p7dH/Mj3QI/LQwwPeAcDuwMzwAfMggHObLTw0gfQRFS8xc90TFP4xL/4hIz8zM="), secret);
            Assert.True(acc.Batches.Count > 0);
            Assert.Equal(PmdMeasurementType.Accelerometer, acc.Batches[0].Type);
            var mag = OfflineRecordingParser.Parse(Convert.FromBase64String("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"), secret);
            Assert.True(mag.Batches.Count > 0);
            Assert.Equal(PmdMeasurementType.Magnetometer, mag.Batches[0].Type);
        }

        private static async Task ControlPoint()
        {
            using var transport = new FakeConnection(); using var pmd = new PmdClient(transport);
            await Assert.ThrowsAsync<PolarFeatureNotAvailableException>(() => pmd.GetSettingsAsync(PmdMeasurementType.Accelerometer));
            await pmd.InitializeAsync(); Assert.Equal(2, transport.Subscriptions.Count);
            Assert.True((await pmd.ReadFeaturesAsync()).Contains(PmdMeasurementType.Accelerometer));
            transport.OnWrite = bytes =>
            {
                Assert.SequenceEqual(Hex("01 02"), bytes);
                transport.EmitControl(Hex("F0 01 02 00 01 00 01"));
                transport.EmitControl(Hex("F0 01 02 00 00 34 00"));
            };
            Assert.Equal(52, (await pmd.GetSettingsAsync(PmdMeasurementType.Accelerometer)).Get(PmdSettingType.SampleRate));
            PmdDataBatch? received = null; pmd.DataReceived += (_, e) => received = e;
            transport.OnWrite = bytes =>
            {
                Assert.Equal((byte)2, bytes[0]);
                transport.EmitControl(Hex("F0 02 02 00 00 05 01 00 00 00 40")); // factor=2, before data callback
                transport.EmitData(Frame(PmdMeasurementType.Accelerometer, 129, Hex("01 00 02 00 03 00")));
            };
            await pmd.StartAsync(PmdMeasurementType.Accelerometer, new PmdSettings(new Dictionary<PmdSettingType, int> { [PmdSettingType.SampleRate] = 52 }));
            Assert.True(received != null); Assert.Equal(2.0, received!.Samples[0].Values[0]);
            transport.OnWrite = bytes => transport.EmitControl(new byte[] { 240, bytes[0], 2, 10 });
            Assert.Equal((byte)10, (await Assert.ThrowsAsync<PmdControlPointException>(() => pmd.StopAsync(PmdMeasurementType.Accelerometer))).StatusCode);
            transport.OnWrite = _ => transport.Disconnect();
            await Assert.ThrowsAsync<PolarDisconnectedException>(() => pmd.StopAsync(PmdMeasurementType.Accelerometer));
            using var timeoutTransport = new FakeConnection(); using var timeoutClient = new PmdClient(timeoutTransport) { CommandTimeout = TimeSpan.FromMilliseconds(25) };
            await timeoutClient.InitializeAsync();
            await Assert.ThrowsAsync<TimeoutException>(() => timeoutClient.GetSdkModeAsync());
            await Assert.ThrowsAsync<PolarProtocolException>(() => timeoutClient.GetSdkModeAsync());
        }
        private sealed class FakeConnection : IBleConnection
        {
            public string DeviceId => "pmd-test"; public bool IsConnected { get; private set; } = true;
            public int MaxWriteSize => 232;
            public event EventHandler<BleNotification>? NotificationReceived;
            public event EventHandler? Disconnected;
            public event EventHandler<BleTransportError>? ErrorOccurred { add { } remove { } }
            public Action<byte[]>? OnWrite { get; set; }
            public List<Guid> Subscriptions { get; } = new List<Guid>();
            public Task<IReadOnlyList<BleGattService>> DiscoverServicesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<BleGattService>>(Array.Empty<BleGattService>());
            public Task<byte[]> ReadAsync(Guid serviceUuid, Guid characteristicUuid, CancellationToken cancellationToken = default) => Task.FromResult(new byte[] { 15, 7, 0 });
            public Task WriteAsync(Guid serviceUuid, Guid characteristicUuid, byte[] data, bool withResponse, CancellationToken cancellationToken = default)
            { OnWrite?.Invoke(data); return Task.CompletedTask; }
            public Task SetNotificationsAsync(Guid serviceUuid, Guid characteristicUuid, bool enabled, CancellationToken cancellationToken = default)
            { if (enabled) Subscriptions.Add(characteristicUuid); else Subscriptions.Remove(characteristicUuid); return Task.CompletedTask; }
            public Task DisconnectAsync(CancellationToken cancellationToken = default) { Disconnect(); return Task.CompletedTask; }
            public void Disconnect() { IsConnected = false; Disconnected?.Invoke(this, EventArgs.Empty); }
            public void EmitControl(byte[] value) => NotificationReceived?.Invoke(this, new BleNotification(PmdClient.ServiceUuid, PmdClient.ControlPointUuid, value));
            public void EmitData(byte[] value) => NotificationReceived?.Invoke(this, new BleNotification(PmdClient.ServiceUuid, PmdClient.DataUuid, value));
            public void Dispose() { }
        }
    }
}
