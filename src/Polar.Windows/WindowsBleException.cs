using System;

namespace PolarBle.Windows
{
    /// <summary>An operating-system BLE error, including an ATT protocol error where available.</summary>
    public sealed class WindowsBleException : PolarException
    {
        public string Operation { get; }
        public byte? AttError { get; }
        public WindowsBleException(string operation, string status, byte? attError = null)
            : base(operation + " failed: " + status + (attError.HasValue ? " (ATT 0x" + attError.Value.ToString("X2") + ")" : ""))
        { Operation = operation; AttError = attError; }
        public WindowsBleException(string operation, Exception inner)
            : base(operation + " failed: " + inner.Message, inner)
        { Operation = operation; }
    }

    public sealed class BleBackgroundErrorEventArgs : EventArgs
    {
        public Exception Exception { get; }
        public BleBackgroundErrorEventArgs(Exception exception) { Exception = exception; }
    }
}
