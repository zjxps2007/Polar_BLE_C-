using System;

namespace PolarBle
{
    public class PolarException : Exception
    {
        public PolarException(string message) : base(message) { }
        public PolarException(string message, Exception inner) : base(message, inner) { }
    }
    public sealed class PolarProtocolException : PolarException
    { public PolarProtocolException(string message) : base(message) { } }
    public sealed class PolarDisconnectedException : PolarException
    { public PolarDisconnectedException(string deviceId) : base("Device disconnected: " + deviceId) { } }
    public sealed class PolarFeatureNotAvailableException : PolarException
    { public PolarFeatureNotAvailableException(string feature) : base("Feature is not available or ready: " + feature) { } }
}
