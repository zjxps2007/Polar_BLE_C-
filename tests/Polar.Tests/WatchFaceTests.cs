using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PolarBle;
using PolarBle.Protocol.Pftp;
using PolarBle.Protocol.Protobuf;
using PolarBle.WatchFaces;

namespace Polar.Tests
{
    internal static class WatchFaceTests
    {
        public static void Run()
        {
            var value = new WatchFaceConfiguration { TimeStyleId = 400, ComplicationLayoutId = 2, BackgroundStyleId = 3, AccentColor = 0xff334455, FontFaceId = 255, ComplicationIds = new[] { -1, 2147483647, 23 } };
            byte[] encoded = WatchFaceCodec.Encode(value);
            var decoded = WatchFaceCodec.Decode(encoded);
            Assert.Equal(value.TimeStyleId, decoded.TimeStyleId); Assert.Equal(value.FontFaceId, decoded.FontFaceId);
            Assert.Equal(value.AccentColor, decoded.AccentColor); Assert.SequenceEqual(value.ComplicationIds, decoded.ComplicationIds);
            var script = WatchFaceCodec.WriteAndCommit(WatchFaceCodec.ConfigurationKey, encoded);
            Assert.SequenceEqual(encoded, WatchFaceCodec.ExtractValue(script, WatchFaceCodec.ConfigurationKey)!);
            Assert.True(WatchFaceCodec.ExtractValue(script, 123) == null);
            // Valid minimal FlatBuffer with no fields, produced independently of the encoder.
            byte[] empty = { 8,0,0,0, 4,0,4,0, 4,0,0,0 };
            Assert.Equal(0U, WatchFaceCodec.Decode(empty).AccentColor);
            Assert.Throws<PolarProtocolException>(() => WatchFaceCodec.Decode(new byte[3]));
            Assert.Throws<PolarProtocolException>(() => WatchFaceCodec.ExtractValue(new byte[] { 0,1,0,0,0,255,255,255,127 }, 1));
            byte[] invalid = (byte[])encoded.Clone(); invalid[40] = 0xff; invalid[41] = 0x7f;
            Assert.Throws<PolarProtocolException>(() => WatchFaceCodec.Decode(invalid));
            foreach (byte command in new byte[] { 3, 4 })
            {
                // Original KvtxScriptUtils tests skip COPY/MOVE of two unrelated keys.
                byte[] copy = { command, 0x11, 0x11, 0x11, 0x11, 0x22, 0x22, 0x22, 0x22 };
                Assert.SequenceEqual(encoded, WatchFaceCodec.ExtractValue(script.Concat(copy).ToArray(), WatchFaceCodec.ConfigurationKey)!);
                Assert.Throws<PolarProtocolException>(() => WatchFaceCodec.ExtractValue(script.Concat(copy.Take(8)).ToArray(), WatchFaceCodec.ConfigurationKey));
                Array.Copy(BitConverter.GetBytes(WatchFaceCodec.ConfigurationKey), 0, copy, 5, 4);
                Assert.Throws<PolarProtocolException>(() => WatchFaceCodec.ExtractValue(script.Concat(copy).ToArray(), WatchFaceCodec.ConfigurationKey));
            }
            ConcurrentWriters().GetAwaiter().GetResult();
        }
        private static async Task ConcurrentWriters()
        {
            using var connection = new FirmwareFakeConnection(); using var client = new PftpClient(connection); await client.InitializeAsync();
            var writes = new List<WatchFaceConfiguration>();
            connection.OnOperation = (operation, payload) =>
            {
                if (operation.Command == PbPFtpOperation.Types.Command.Get) return null;
                writes.Add(WatchFaceCodec.Decode(WatchFaceCodec.ExtractValue(payload, WatchFaceCodec.ConfigurationKey)!));
                return Array.Empty<byte>();
            };
            var service = new WatchFaceService(client); var ids = new[] { 7 };
            var complications = service.SetComplicationsAsync(ids); ids[0] = 99;
            var full = service.SetAsync(new WatchFaceConfiguration { AccentColor = 100, ComplicationIds = new[] { 8 } });
            Assert.Equal(1, connection.Operations.Count);
            connection.Respond(WatchFaceCodec.WriteAndCommit(WatchFaceCodec.ConfigurationKey,
                WatchFaceCodec.Encode(new WatchFaceConfiguration { TimeStyleId = 42, AccentColor = 88, ComplicationIds = new[] { 1 } })));
            await Task.WhenAll(complications, full);
            Assert.Equal(2, writes.Count); Assert.Equal((ushort)42, writes[0].TimeStyleId); Assert.Equal(88U, writes[0].AccentColor);
            Assert.SequenceEqual(new[] { 7 }, writes[0].ComplicationIds); Assert.Equal(100U, writes[1].AccentColor); Assert.SequenceEqual(new[] { 8 }, writes[1].ComplicationIds);
        }
    }
}
