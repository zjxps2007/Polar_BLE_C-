using System;
using System.Threading;
using System.Threading.Tasks;

namespace PolarBle.Transport
{
    public enum BleRadioState { Unknown = 0, On = 1, Off = 2, Disabled = 3, Unavailable = 4 }

    public sealed class BleRadioStateChanged : EventArgs
    {
        public BleRadioState State { get; }
        public BleRadioStateChanged(BleRadioState state) { State = state; }
    }

    /// <summary>Optional, read-only adapter state. This interface never changes OS radio power.</summary>
    public interface IBleRadioStateProvider
    {
        event EventHandler<BleRadioStateChanged>? RadioStateChanged;
        /// <summary>Starts state observation and returns the current aggregate Bluetooth radio state.</summary>
        Task<BleRadioState> GetRadioStateAsync(CancellationToken cancellationToken = default);
    }
}
