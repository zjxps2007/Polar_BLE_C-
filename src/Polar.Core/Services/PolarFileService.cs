using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using PolarBle.Protocol.Pftp;
using PolarBle.Protocol.Protobuf;

namespace PolarBle.Services
{
    public sealed class PolarFileEntry
    {
        public string Path { get; }
        public ulong Size { get; }
        public bool IsDirectory => Path.EndsWith("/", StringComparison.Ordinal);
        public PbPFtpEntry Metadata { get; }
        public PolarFileEntry(string path, PbPFtpEntry metadata) { Path = path; Size = metadata.Size; Metadata = metadata; }
    }

    /// <summary>Device file access. These are Polar's experimental low-level operations.</summary>
    public sealed class PolarFileService
    {
        private readonly PftpClient _client;
        public PolarFileService(PftpClient client) { _client = client ?? throw new ArgumentNullException(nameof(client)); }
        public Task<byte[]> ReadAsync(string path, CancellationToken cancellationToken = default, IProgress<long>? progress = null)
            => _client.RequestAsync(Operation(path, PbPFtpOperation.Types.Command.Get), cancellationToken, progress);
        public Task WriteAsync(string path, byte[] data, CancellationToken cancellationToken = default, IProgress<long>? progress = null)
            => _client.WriteAsync(Operation(path, PbPFtpOperation.Types.Command.Put), data, cancellationToken, progress);
        public Task MergeAsync(string path, byte[] data, CancellationToken cancellationToken = default)
            => _client.WriteAsync(Operation(path, PbPFtpOperation.Types.Command.Merge), data, cancellationToken);
        public async Task DeleteAsync(string path, CancellationToken cancellationToken = default)
            => await _client.RequestAsync(Operation(path, PbPFtpOperation.Types.Command.Remove), cancellationToken).ConfigureAwait(false);
        public Task CreateFolderAsync(string path, CancellationToken cancellationToken = default)
            => WriteAsync(DirectoryPath(path), Array.Empty<byte>(), cancellationToken);
        public async Task<T> ReadMessageAsync<T>(string path, MessageParser<T> parser, CancellationToken cancellationToken = default) where T : IMessage<T>
            => parser.ParseFrom(await ReadAsync(path, cancellationToken).ConfigureAwait(false));
        public Task WriteMessageAsync(string path, IMessage message, CancellationToken cancellationToken = default)
            => WriteAsync(path, message.ToByteArray(), cancellationToken);
        public async Task<IReadOnlyList<PolarFileEntry>> ListAsync(string path = "/", bool recursive = false, CancellationToken cancellationToken = default)
        {
            var output = new List<PolarFileEntry>();
            var pending = new Queue<string>(); var visited = new HashSet<string>(StringComparer.Ordinal);
            pending.Enqueue(DirectoryPath(path));
            while (pending.Count != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string directory = pending.Dequeue();
                if (!visited.Add(directory)) continue;
                if (visited.Count > 10000) throw new PolarProtocolException("Directory traversal limit exceeded.");
                var listing = await ReadMessageAsync(directory, PbPFtpDirectory.Parser, cancellationToken).ConfigureAwait(false);
                foreach (var entry in listing.Entries)
                {
                    if (entry.Name.Length == 0 || entry.Name == "." || entry.Name == ".." || entry.Name.Contains("../") || entry.Name.StartsWith("/", StringComparison.Ordinal))
                        throw new PolarProtocolException("Device returned an invalid directory entry.");
                    var item = new PolarFileEntry(directory + entry.Name, entry);
                    output.Add(item);
                    if (output.Count > 100000) throw new PolarProtocolException("File enumeration limit exceeded.");
                    if (recursive && item.IsDirectory) pending.Enqueue(item.Path);
                }
            }
            return output;
        }
        internal static string DirectoryPath(string path)
        { ValidatePath(path); return path.EndsWith("/", StringComparison.Ordinal) ? path : path + "/"; }
        private static byte[] Operation(string path, PbPFtpOperation.Types.Command command)
        { ValidatePath(path); return new PbPFtpOperation { Command = command, Path = path }.ToByteArray(); }
        private static void ValidatePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path[0] != '/' || path.IndexOf('\0') >= 0)
                throw new ArgumentException("Use an absolute Polar device path beginning with '/'.", nameof(path));
        }
    }
}
