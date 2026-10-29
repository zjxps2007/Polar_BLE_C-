using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PolarBle.Models;
using PolarBle.Protocol.Pftp;
using PolarBle.Protocol.Protobuf;

namespace PolarBle.Services
{
    public sealed class PolarLoggingService
    {
        private readonly PolarFileService _files;
        public PolarLoggingService(PolarFileService files) { _files = files; }
        public Task<PbSensorDataLog> GetConfigurationAsync(CancellationToken cancellationToken = default)
            => _files.ReadMessageAsync("/SDLOGS.BPB", PbSensorDataLog.Parser, cancellationToken);
        public Task SetConfigurationAsync(PbSensorDataLog configuration, CancellationToken cancellationToken = default)
            => _files.WriteMessageAsync("/SDLOGS.BPB", configuration, cancellationToken);
        public async Task<PolarLogConfiguration> GetLogConfigurationAsync(CancellationToken cancellationToken = default)
            => new PolarLogConfiguration(await GetConfigurationAsync(cancellationToken).ConfigureAwait(false));
        public Task SetLogConfigurationAsync(PolarLogConfiguration configuration, CancellationToken cancellationToken = default)
            => SetConfigurationAsync((configuration ?? throw new ArgumentNullException(nameof(configuration))).ToProto(), cancellationToken);
        public async Task DeleteTelemetryDataAsync(CancellationToken cancellationToken = default)
        {
            foreach (var entry in (await _files.ListAsync("/", false, cancellationToken).ConfigureAwait(false)).Where(f => Regex.IsMatch(f.Path, @"^/TRC\d{1,3}\.BIN$", RegexOptions.CultureInvariant)))
                await _files.DeleteAsync(entry.Path, cancellationToken).ConfigureAwait(false);
        }
        public async Task<IReadOnlyList<PolarDeviceLog>> ExportAsync(CancellationToken cancellationToken = default)
        {
            var output = new List<PolarDeviceLog>();
            foreach (string path in new[] { "/ERRORLOG.BPB", "/ERRORLO2.BPB", "/SYSLOG.TXT" })
            {
                try { output.Add(new PolarDeviceLog(path, await _files.ReadAsync(path, cancellationToken).ConfigureAwait(false))); }
                catch (PftpResponseException error) when (error.ErrorCode == 103) { }
            }
            foreach (string prefix in new[] { "/TRC", "/DBGTRC" })
            {
                for (int index = 1; index <= 10000; index++)
                {
                    string path = prefix + index + ".BIN";
                    try { output.Add(new PolarDeviceLog(path, await _files.ReadAsync(path, cancellationToken).ConfigureAwait(false))); }
                    catch (PftpResponseException error) when (error.ErrorCode == 103) { break; }
                    if (index == 10000) throw new PolarProtocolException("Device log enumeration limit exceeded.");
                }
            }
            return output;
        }
    }
}
