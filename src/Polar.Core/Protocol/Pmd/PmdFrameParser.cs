// Ported from Polar BLE SDK. Copyright Polar Electro Oy. See LICENSES/Polar_SDK_License.txt.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PolarBle.Protocol.Pmd
{
    /// <summary>Pure managed decoder for the raw and delta formats implemented by upstream Polar SDK.</summary>
    public static class PmdFrameParser
    {
        public static IReadOnlyList<int[]> DecodeDelta(byte[] payload, int channels, int resolution, bool signed = true)
        {
            if (channels < 1 || channels > 255 || resolution < 1 || resolution > 32) throw new ArgumentOutOfRangeException();
            int width = (resolution + 7) / 8, offset = channels * width;
            PmdBinary.Require(payload, 0, offset);
            var reference = new int[channels];
            for (int ch = 0; ch < channels; ch++)
            {
                int value = unchecked((int)PmdBinary.Unsigned(payload, ch * width, width));
                if (signed && resolution < 32) value = value << (32 - resolution) >> (32 - resolution);
                reference[ch] = value;
            }
            var samples = new List<int[]> { reference };
            while (offset < payload.Length)
            {
                PmdBinary.Require(payload, offset, 2); int bits = payload[offset++], count = payload[offset++];
                if (bits < 1 || bits > 32 || count == 0) throw new PolarProtocolException("Invalid delta block width/count.");
                int byteLength = (bits * count * channels + 7) / 8;
                PmdBinary.Require(payload, offset, byteLength); int bitOffset = 0;
                for (int s = 0; s < count; s++)
                {
                    var next = new int[channels]; var previous = samples[samples.Count - 1];
                    for (int ch = 0; ch < channels; ch++)
                    {
                        uint value = 0;
                        for (int bit = 0; bit < bits; bit++, bitOffset++)
                            value |= (uint)((payload[offset + bitOffset / 8] >> (bitOffset % 8)) & 1) << bit;
                        int delta = unchecked((int)value);
                        if (bits < 32) delta = delta << (32 - bits) >> (32 - bits);
                        next[ch] = unchecked(previous[ch] + delta);
                    }
                    samples.Add(next);
                }
                offset += byteLength;
            }
            return samples;
        }

        public static ulong[] Timestamps(ulong previous, ulong last, int count, int sampleRate)
        {
            if (count <= 0) throw new PolarProtocolException("PMD frame has no samples.");
            if (previous != 0 && last <= previous) throw new PolarProtocolException("Non-increasing PMD timestamp.");
            if (previous == 0 && sampleRate <= 0) throw new PolarProtocolException("Sample rate is required for first PMD frame.");
            // Decimal arithmetic preserves low bits of nanosecond timestamps at the Polar epoch.
            decimal delta = previous == 0 ? 1_000_000_000m / sampleRate : (decimal)(last - previous) / count;
            decimal first = previous == 0 ? last - delta * (count - 1) : previous + delta;
            if (first < 0) throw new PolarProtocolException("PMD sample timestamp precedes device epoch.");
            var result = new ulong[count];
            for (int i = 0; i < count - 1; i++) result[i] = (ulong)decimal.Round(first + delta * i, 0, MidpointRounding.ToEven);
            result[count - 1] = last; return result;
        }

        public static PmdDataBatch Parse(byte[] frame, int sampleRate = 0, float factor = 1,
            ulong previousTimestamp = 0, IReadOnlyCollection<int>? derivedMethods = null)
        {
            PmdBinary.Require(frame, 0, 10);
            var type = (PmdMeasurementType)(frame[0] & 63); byte format = (byte)(frame[9] & 127);
            bool compressed = (frame[9] & 128) != 0; ulong timestamp = PmdBinary.Unsigned(frame, 1, 8);
            var data = PmdBinary.Slice(frame, 10, frame.Length - 10);
            if (data.Length == 0) throw new PolarProtocolException("Empty PMD data frame.");
            List<PmdSample> samples; string[] names;
            if (type == PmdMeasurementType.DerivedMeasurement ||
                (type == PmdMeasurementType.Accelerometer && derivedMethods != null && derivedMethods.Count > 0))
            {
                if (compressed) throw Unsupported(type, format, compressed);
                samples = ParseDerived(data, type, format, timestamp, previousTimestamp, derivedMethods, out names);
            }
            else switch (type)
            {
                case PmdMeasurementType.Ecg:
                    if (compressed || format > 3) throw Unsupported(type, format, compressed);
                    names = format == 3 ? new[] { "data0", "data1" } : new[] { "microvolts" };
                    int ecgSize = format == 3 ? 7 : 3;
                    samples = Fixed(data, ecgSize, timestamp, previousTimestamp, sampleRate, (o, t) =>
                    {
                        var meta = new Dictionary<string, object>(); double[] values;
                        if (format == 0) values = new[] { (double)PmdBinary.Signed(data, o, 3) };
                        else if (format == 1)
                        {
                            values = new[] { (double)(data[o] | (data[o + 1] & 63) << 8) };
                            meta["oversampling"] = (data[o + 2] & 1) != 0; meta["skinContact"] = (data[o + 2] >> 1) & 3;
                            meta["contactImpedance"] = (data[o + 2] >> 3) & 3;
                        }
                        else if (format == 2)
                        {
                            values = new[] { (double)(data[o] | data[o + 1] << 8 | (data[o + 2] & 3) << 16) };
                            meta["ecgDataTag"] = (data[o + 2] >> 2) & 7; meta["paceDataTag"] = data[o + 2] >> 5;
                        }
                        else { values = new[] { (double)PmdBinary.Signed(data, o, 3), PmdBinary.Signed(data, o + 3, 3) }; meta["status"] = data[o + 6]; }
                        return new PmdSample(t, values, meta);
                    }); break;
                case PmdMeasurementType.Accelerometer:
                    names = new[] { "x_milliG", "y_milliG", "z_milliG" };
                    if (compressed && format <= 1)
                    {
                        float scale = format == 0 ? factor * 1000 : factor;
                        samples = Delta(data, 3, 16, true, timestamp, previousTimestamp, sampleRate,
                            (s, t) => new PmdSample(t, s.Select(v => (double)(int)(v * scale)).ToArray()));
                    }
                    else if (!compressed && format <= 2)
                    {
                        int width = format + 1;
                        samples = Fixed(data, width * 3, timestamp, previousTimestamp, sampleRate,
                            (o, t) => new PmdSample(t, Enumerable.Range(0, 3).Select(ch => (double)PmdBinary.Signed(data, o + ch * width, width)).ToArray()));
                    }
                    else throw Unsupported(type, format, compressed);
                    break;
                case PmdMeasurementType.Gyroscope:
                    if (!compressed || format > 1) throw Unsupported(type, format, compressed);
                    names = new[] { "x_deg_s", "y_deg_s", "z_deg_s" };
                    samples = Delta(data, 3, format == 0 ? 16 : 32, format == 0, timestamp, previousTimestamp, sampleRate,
                        (s, t) => new PmdSample(t, s.Select(v => (double)((format == 0 ? v : PmdBinary.Float(v)) * factor)).ToArray())); break;
                case PmdMeasurementType.Magnetometer:
                    if (!compressed || format > 1) throw Unsupported(type, format, compressed);
                    names = new[] { "x_gauss", "y_gauss", "z_gauss" };
                    samples = Delta(data, format == 0 ? 3 : 4, 16, true, timestamp, previousTimestamp, sampleRate,
                        (s, t) => new PmdSample(t, s.Take(3).Select(v => (double)(v * factor / (format == 0 ? 1 : 1000))).ToArray(),
                            new Dictionary<string, object> { ["calibrationStatus"] = format == 0 ? -1 : s[3] })); break;
                case PmdMeasurementType.SkinTemperature: case PmdMeasurementType.Temperature: case PmdMeasurementType.Pressure:
                    if (format != 0) throw Unsupported(type, format, compressed);
                    names = new[] { type == PmdMeasurementType.Pressure ? "bar" : "celsius" };
                    samples = compressed ? Delta(data, 1, 32, false, timestamp, previousTimestamp, sampleRate,
                        (s, t) => new PmdSample(t, new[] { (double)(PmdBinary.Float(s[0]) * factor) }))
                        : Fixed(data, 4, timestamp, previousTimestamp, sampleRate, (o, t) => new PmdSample(t, new[] { (double)PmdBinary.Float(data, o) })); break;
                case PmdMeasurementType.Ppi:
                    if (compressed || format != 0) throw Unsupported(type, format, compressed);
                    Multiple(data, 6); names = new[] { "heartRate_bpm", "ppi_ms", "errorEstimate_ms" }; samples = new List<PmdSample>();
                    ulong current = timestamp;
                    for (int o = data.Length - 6; o >= 0; o -= 6)
                    {
                        ulong ppi = PmdBinary.Unsigned(data, o + 1, 2);
                        samples.Add(new PmdSample(current, new[] { (double)data[o], ppi, PmdBinary.Unsigned(data, o + 3, 2) },
                            new Dictionary<string, object> { ["blocker"] = (data[o + 5] & 1) != 0, ["skinContact"] = (data[o + 5] & 2) != 0, ["skinContactSupported"] = (data[o + 5] & 4) != 0 }));
                        if (timestamp != 0 && o > 0)
                        { ulong delta = ppi * 1_000_000; if (delta > current) throw new PolarProtocolException("PPI timestamp underflow."); current -= delta; }
                    }
                    samples.Reverse(); break;
                case PmdMeasurementType.OfflineHeartRate:
                    if (compressed || format > 1) throw Unsupported(type, format, compressed);
                    int hrWidth = format == 0 ? 1 : 3; Multiple(data, hrWidth);
                    names = format == 0 ? new[] { "heartRate_bpm" } : new[] { "heartRate_bpm", "ppgQuality", "correctedHeartRate_bpm" };
                    samples = new List<PmdSample>();
                    for (int o = 0; o < data.Length; o += hrWidth)
                        samples.Add(new PmdSample(0, Enumerable.Range(0, hrWidth).Select(ch => (double)data[o + ch]).ToArray()));
                    break;
                case PmdMeasurementType.Ppg:
                    samples = ParsePpg(data, format, compressed, timestamp, previousTimestamp, sampleRate, factor, out names); break;
                case PmdMeasurementType.Location:
                    if (compressed || format > 3) throw Unsupported(type, format, compressed);
                    samples = ParseLocation(data, format, timestamp, previousTimestamp, sampleRate, out names); break;
                default: throw Unsupported(type, format, compressed);
            }
            return new PmdDataBatch(type, format, compressed, timestamp, names, samples, frame);
        }

        private static List<PmdSample> ParsePpg(byte[] data, byte format, bool compressed, ulong timestamp,
            ulong previous, int rate, float factor, out string[] names)
        {
            if (compressed)
            {
                int channels;
                switch (format) { case 0: channels = 4; break; case 7: channels = 17; break; case 8: channels = 25; break; case 10: channels = 21; break; case 13: channels = 3; break; default: throw Unsupported(PmdMeasurementType.Ppg, format, true); }
                int signals = format == 0 ? 4 : channels - 1;
                names = Enumerable.Range(0, signals).Select(i => format == 10 ? (i < 8 ? "red" + i : i < 14 ? "green" + (i - 8) : "ir" + (i - 14)) : format == 0 && i == 3 ? "ambient" : "ppg" + i).ToArray();
                return Delta(data, channels, 24, true, timestamp, previous, rate, (s, t) => new PmdSample(t,
                    s.Take(signals).Select(v => (double)(int)(v * factor)).ToArray(),
                    format == 0 ? null : new Dictionary<string, object> { ["status"] = s[channels - 1] & 0xFFFFFF }));
            }
            switch (format)
            {
                case 0:
                    names = new[] { "ppg0", "ppg1", "ppg2", "ambient" };
                    return Fixed(data, 12, timestamp, previous, rate, (o, t) => new PmdSample(t, Enumerable.Range(0, 4).Select(ch => (double)PmdBinary.Signed(data, o + ch * 3, 3)).ToArray()));
                case 4: case 9: case 14:
                    int count = format == 14 ? 1 : 12; names = Array.Empty<string>();
                    return Fixed(data, count * 3, timestamp, previous, rate, (o, t) => new PmdSample(t, Array.Empty<double>(), new Dictionary<string, object>
                    {
                        ["integrationTimes"] = Enumerable.Range(0, count).Select(i => (int)data[o + i]).ToArray(),
                        ["channel0Gains"] = Enumerable.Range(0, count).Select(i => (int)(data[o + count + 2 * i] & 7)).ToArray(),
                        ["channel1Gains"] = Enumerable.Range(0, count).Select(i => (int)(data[o + count + 2 * i + 1] & 7)).ToArray()
                    }));
                case 5:
                    names = new[] { "operationMode" };
                    return Fixed(data, 4, timestamp, previous, rate, (o, t) => new PmdSample(t, new[] { (double)PmdBinary.Unsigned(data, o, 4) }));
                case 6:
                    names = Array.Empty<string>();
                    return Fixed(data, 8, timestamp, previous, rate, (o, t) => new PmdSample(t, Array.Empty<double>(), new Dictionary<string, object> { ["sportId"] = PmdBinary.Unsigned(data, o, 8) }));
                default: throw Unsupported(PmdMeasurementType.Ppg, format, false);
            }
        }

        private static List<PmdSample> ParseLocation(byte[] data, byte format, ulong timestamp, ulong previous, int rate, out string[] names)
        {
            if (format == 0)
            {
                names = new[] { "latitude", "longitude", "distance_m", "speed_m_s", "usedAccelerationSpeed", "coordinateSpeed", "accelerationSpeedFactor", "course_deg", "gpsChipSpeed" };
                return Fixed(data, 51, timestamp, previous, rate, (o, t) => new PmdSample(t, new[]
                {
                    PmdBinary.Double(data,o), PmdBinary.Double(data,o+8), PmdBinary.Unsigned(data,o+24,4)/10.0,
                    PmdBinary.Float(data,o+28), PmdBinary.Float(data,o+32), PmdBinary.Float(data,o+36), PmdBinary.Float(data,o+40),
                    PmdBinary.Unsigned(data,o+44,2)/100.0, PmdBinary.Unsigned(data,o+46,2)/100.0
                }, new Dictionary<string, object>
                {
                    ["year"] = PmdBinary.Unsigned(data,o+16,2), ["month"] = data[o+18], ["day"] = data[o+19],
                    ["time"] = PmdBinary.Unsigned(data,o+20,4), ["fix"] = data[o+48] != 0,
                    ["speedFlag"] = (sbyte)data[o+49], ["fusionState"] = data[o+50]
                }));
            }
            if (format == 1)
            {
                names = new[] { "dilution", "altitude_m", "satellites" };
                return Fixed(data, 6, timestamp, previous, rate, (o, t) => new PmdSample(t,
                    new[] { PmdBinary.Unsigned(data, o, 2) / 100.0, PmdBinary.Signed(data, o + 2, 2), (double)data[o + 4] },
                    new Dictionary<string, object> { ["fix"] = data[o + 5] != 0 }));
            }
            if (format == 2)
            {
                names = new[] { "maxSnr" };
                return Fixed(data, 41, timestamp, previous, rate, (o, t) => new PmdSample(t, new[] { (double)data[o + 40] },
                    new Dictionary<string, object> { ["seenBand1"] = PmdBinary.Slice(data,o,10), ["usedBand1"] = PmdBinary.Slice(data,o+10,10), ["seenBand2"] = PmdBinary.Slice(data,o+20,10), ["usedBand2"] = PmdBinary.Slice(data,o+30,10) }));
            }
            names = Array.Empty<string>(); var result = new List<PmdSample>(); int offset = 0;
            while (offset < data.Length)
            {
                PmdBinary.Require(data, offset, 7); uint period = (uint)PmdBinary.Unsigned(data, offset, 4); int length = (int)PmdBinary.Unsigned(data, offset + 4, 2); byte status = data[offset + 6]; offset += 7;
                var message = Encoding.UTF8.GetString(PmdBinary.Slice(data, offset, length)); offset += length;
                result.Add(new PmdSample(timestamp, Array.Empty<double>(), new Dictionary<string, object> { ["measurementPeriod"] = period, ["status"] = status, ["nmea"] = message }));
            }
            return result;
        }
        private static List<PmdSample> ParseDerived(byte[] data, PmdMeasurementType type, byte format, ulong timestamp,
            ulong previous, IReadOnlyCollection<int>? methods, out string[] names)
        {
            int width = format == 0 ? 2 : format == 1 ? 4 : format == 2 ? 3 : throw Unsupported(type, format, false);
            bool spec = type == PmdMeasurementType.DerivedMeasurement; int offset = 0; int[] ids;
            if (spec)
            {
                // Upstream tests establish: inner derived frame type, source type, uint16 method bits.
                PmdBinary.Require(data, 0, 4); if (data[0] != 0 || (data[1] & 63) != (byte)PmdMeasurementType.Accelerometer) throw new PolarProtocolException("Unsupported derived source measurement.");
                int bits = (int)PmdBinary.Unsigned(data, 2, 2); ids = Enumerable.Range(0, 16).Where(i => (bits & (1 << i)) != 0).ToArray(); offset = 4;
            }
            else ids = methods?.OrderBy(i => i).ToArray() ?? Array.Empty<int>();
            if (ids.Length == 0 || ids.Any(i => i < 0 || i > 9)) throw new PolarProtocolException("Unknown or missing derived methods.");
            int components = spec ? 4 : 3;
            int valuesPerSample = ids.Sum(id => id <= components ? 3 : 1), sampleBytes = valuesPerSample * width;
            if ((data.Length - offset) <= 0 || (data.Length - offset) % sampleBytes != 0) throw new PolarProtocolException("Truncated derived measurement sample.");
            int count = (data.Length - offset) / sampleBytes; names = ids.SelectMany(id => Enumerable.Range(0, id <= components ? 3 : 1).Select(ch => "method" + id + "_" + ch)).ToArray();
            if (previous != 0 && timestamp < previous) throw new PolarProtocolException("Non-increasing derived timestamp.");
            var result = new List<PmdSample>();
            for (int s = 0; s < count; s++)
            {
                var values = new List<double>(); var metadata = new Dictionary<string, object>();
                foreach (int id in ids)
                {
                    int channels = id <= components ? 3 : 1; var methodValues = new double[channels];
                    for (int ch = 0; ch < channels; ch++, offset += width)
                        methodValues[ch] = id >= 4 ? PmdBinary.Unsigned(data, offset, width) : PmdBinary.Signed(data, offset, width);
                    values.AddRange(methodValues); metadata["method" + id] = methodValues;
                }
                ulong t = count == 1 || previous == 0 ? timestamp : previous + (ulong)((decimal)(timestamp - previous) * (s + 1) / count);
                result.Add(new PmdSample(t, values, metadata));
            }
            return result;
        }
        private static List<PmdSample> Delta(byte[] data, int channels, int bits, bool signed, ulong last, ulong previous, int rate, Func<int[], ulong, PmdSample> map)
        {
            var raw = DecodeDelta(data, channels, bits, signed); var times = Timestamps(previous, last, raw.Count, rate); var result = new List<PmdSample>(raw.Count);
            for (int i = 0; i < raw.Count; i++) result.Add(map(raw[i], times[i])); return result;
        }
        private static List<PmdSample> Fixed(byte[] data, int width, ulong last, ulong previous, int rate, Func<int, ulong, PmdSample> map)
        {
            Multiple(data, width); int count = data.Length / width; var times = Timestamps(previous, last, count, rate); var result = new List<PmdSample>(count);
            for (int i = 0; i < count; i++) result.Add(map(i * width, times[i])); return result;
        }
        private static void Multiple(byte[] data, int width)
        { if (data.Length == 0 || data.Length % width != 0) throw new PolarProtocolException("PMD payload is not a complete sample sequence."); }
        private static PolarProtocolException Unsupported(PmdMeasurementType type, byte format, bool compressed)
            => new PolarProtocolException("Unsupported PMD frame: " + type + ", type " + format + ", compressed=" + compressed);
    }
}
