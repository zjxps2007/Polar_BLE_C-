using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PolarBle.Transport;
using Windows.Devices.Bluetooth;
using Windows.Devices.Radios;

namespace PolarBle.Windows
{
    public sealed partial class WindowsBleTransport : IBleRadioStateProvider
    {
        private readonly object _radioGate = new();
        private Radio[] _radios = Array.Empty<Radio>();
        private Task<BleRadioState>? _radioInitialization;
        private BleRadioState _lastRadioState;
        public event EventHandler<BleRadioStateChanged>? RadioStateChanged;

        /// <summary>Reads and observes Bluetooth radios. Never changes Windows radio power.</summary>
        public Task<BleRadioState> GetRadioStateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task<BleRadioState> pending;
            lock (_radioGate)
            {
                ThrowIfDisposed();
                if (_radios.Length != 0) return Task.FromResult(ReadAggregateRadioState());
                // Retry discovery on a subsequent query if no adapter/radio was available.
                if (_radioInitialization == null || _radioInitialization.IsCompleted)
                    _radioInitialization = InitializeRadioAsync();
                pending = _radioInitialization;
            }
            return pending.WaitAsync(cancellationToken);
        }

        private async Task<BleRadioState> InitializeRadioAsync()
        {
            await Task.Yield();
            try
            {
                var available = await Radio.GetRadiosAsync().AsTask().ConfigureAwait(false);
                var radios = available.Where(radio => radio.Kind == RadioKind.Bluetooth).ToArray();
                if (radios.Length == 0)
                {
                    var adapter = await BluetoothAdapter.GetDefaultAsync().AsTask().ConfigureAwait(false);
                    // A present adapter whose radio cannot be read is not evidence of power-off.
                    return PublishRadioState(adapter == null ? BleRadioState.Unavailable : BleRadioState.Unknown);
                }
                BleRadioState state;
                lock (_radioGate)
                {
                    if (Volatile.Read(ref _disposed) != 0) return BleRadioState.Unknown;
                    _radios = radios;
                    foreach (var radio in radios) radio.StateChanged += OnRadioStateChanged;
                    state = ReadAggregateRadioState();
                }
                return PublishRadioState(state);
            }
            catch (Exception exception)
            {
                DisposeRadioObservation();
                if (Volatile.Read(ref _disposed) == 0) ReportBackgroundError(new WindowsBleException("Read Bluetooth radio state", exception));
                return PublishRadioState(BleRadioState.Unknown);
            }
        }

        private void OnRadioStateChanged(Radio sender, object args)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try
            {
                BleRadioState state;
                lock (_radioGate) state = ReadAggregateRadioState();
                PublishRadioState(state);
            }
            catch (Exception exception) { ReportBackgroundError(exception); }
        }

        private BleRadioState PublishRadioState(BleRadioState state)
        {
            bool changed;
            lock (_radioGate)
            {
                if (Volatile.Read(ref _disposed) != 0) return state;
                changed = _lastRadioState != state;
                _lastRadioState = state;
            }
            if (changed)
                try { RadioStateChanged?.Invoke(this, new BleRadioStateChanged(state)); }
                catch (Exception exception) { ReportBackgroundError(exception); }
            return state;
        }

        private BleRadioState ReadAggregateRadioState()
        {
            if (_radios.Length == 0) return BleRadioState.Unknown;
            var states = _radios.Select(radio => radio.State).ToArray();
            if (states.Contains(RadioState.On)) return BleRadioState.On;
            if (states.Contains(RadioState.Unknown)) return BleRadioState.Unknown;
            if (states.Contains(RadioState.Off)) return BleRadioState.Off;
            return states.All(state => state == RadioState.Disabled) ? BleRadioState.Disabled : BleRadioState.Unknown;
        }

        private void DisposeRadioObservation()
        {
            lock (_radioGate)
            {
                foreach (var radio in _radios)
                {
                    try { radio.StateChanged -= OnRadioStateChanged; }
                    catch (Exception exception) { ReportBackgroundError(exception); }
                }
                _radios = Array.Empty<Radio>();
            }
        }
    }
}
