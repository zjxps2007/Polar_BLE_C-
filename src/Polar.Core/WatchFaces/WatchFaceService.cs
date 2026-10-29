using System;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using PolarBle.Protocol.Pftp;
using PolarBle.Protocol.Protobuf;

namespace PolarBle.WatchFaces
{
    public sealed class WatchFaceService
    {
        private const string Path = "/SYS/KVTX";
        private readonly PftpClient _client;
        private readonly SemaphoreSlim _changes = new SemaphoreSlim(1, 1);
        public WatchFaceService(PftpClient client) { _client = client; }
        public async Task<WatchFaceConfiguration> GetAsync(CancellationToken cancellationToken = default)
        {
            var bytes = await _client.RequestAsync(new PbPFtpOperation { Command = PbPFtpOperation.Types.Command.Get, Path = Path }.ToByteArray(), cancellationToken).ConfigureAwait(false);
            var value = WatchFaceCodec.ExtractValue(bytes, WatchFaceCodec.ConfigurationKey);
            return value == null ? new WatchFaceConfiguration() : WatchFaceCodec.Decode(value);
        }
        public async Task SetAsync(WatchFaceConfiguration configuration, CancellationToken cancellationToken = default)
        {
            var script = WatchFaceCodec.WriteAndCommit(WatchFaceCodec.ConfigurationKey, WatchFaceCodec.Encode(configuration));
            await _changes.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { await WriteScriptAsync(script, cancellationToken).ConfigureAwait(false); }
            finally { _changes.Release(); }
        }
        private Task WriteScriptAsync(byte[] script, CancellationToken cancellationToken)
            => _client.WriteAsync(new PbPFtpOperation { Command = PbPFtpOperation.Types.Command.Put, Path = Path }.ToByteArray(), script, cancellationToken);
        public async Task SetComplicationsAsync(int[] complicationIds, CancellationToken cancellationToken = default)
        {
            if (complicationIds == null) throw new ArgumentNullException(nameof(complicationIds));
            var snapshot = (int[])complicationIds.Clone();
            await _changes.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var configuration = await GetAsync(cancellationToken).ConfigureAwait(false); configuration.ComplicationIds = snapshot;
                await WriteScriptAsync(WatchFaceCodec.WriteAndCommit(WatchFaceCodec.ConfigurationKey, WatchFaceCodec.Encode(configuration)), cancellationToken).ConfigureAwait(false);
            }
            finally { _changes.Release(); }
        }
    }
}
