using System;
using System.Threading;
using System.Threading.Tasks;
using PolarBle.Transport;

namespace PolarBle
{
    public sealed partial class PolarClient
    {
        private IBleRadioStateProvider? _radioProvider;
        private int _radioState;
        /// <summary>Last observed state. Unknown until the transport has supplied a value.</summary>
        public BleRadioState BluetoothRadioState => (BleRadioState)Volatile.Read(ref _radioState);
        /// <summary>Null means the state is unknown; absence of a Bluetooth adapter returns false.</summary>
        public bool? IsBlePowered => BluetoothRadioState == BleRadioState.Unknown ? (bool?)null : BluetoothRadioState == BleRadioState.On;
        public event EventHandler<BleRadioStateChanged>? BluetoothRadioStateChanged;

        public async Task<BleRadioState> GetBluetoothRadioStateAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            var state = _radioProvider == null ? BleRadioState.Unknown : await _radioProvider.GetRadioStateAsync(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            UpdateRadioState(state);
            return state;
        }

        private void InitializeRadioState()
        {
            _radioProvider = _transport as IBleRadioStateProvider;
            if (_radioProvider == null) return;
            _radioProvider.RadioStateChanged += OnRadioStateChanged;
            _ = ObserveInitialRadioStateAsync();
        }

        private async Task ObserveInitialRadioStateAsync()
        {
            try { await GetBluetoothRadioStateAsync(_lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (!_disposed) try { OnError(this, new BleTransportError("Bluetooth radio state", error)); } catch { }
            }
        }

        private void OnRadioStateChanged(object? sender, BleRadioStateChanged change) => UpdateRadioState(change.State);
        private void UpdateRadioState(BleRadioState state)
        {
            if (_disposed) return;
            if (Interlocked.Exchange(ref _radioState, (int)state) != (int)state)
                BluetoothRadioStateChanged?.Invoke(this, new BleRadioStateChanged(state));
        }

        private void DisposeRadioState()
        {
            if (_radioProvider != null) _radioProvider.RadioStateChanged -= OnRadioStateChanged;
        }
    }
}
