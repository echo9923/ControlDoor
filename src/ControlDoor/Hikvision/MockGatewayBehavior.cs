using System;
using System.Threading;
using System.Threading.Tasks;

namespace ControlDoor.Hikvision
{
    public sealed class MockGatewayBehavior
    {
        public Func<object, object> ResultFactory { get; set; }

        public Exception Exception { get; set; }

        public TimeSpan Delay { get; set; }

        public bool Timeout { get; set; }

        public async Task ApplyAsync(CancellationToken cancellationToken)
        {
            if (Timeout)
            {
                throw new DeviceGatewayException("MockGateway", SdkError.FromHttpStatusCode(408, "模拟超时"));
            }

            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
            }

            if (Exception != null)
            {
                throw Exception;
            }
        }

        public T Resolve<T>(object request, Func<T> fallback)
        {
            if (ResultFactory == null)
            {
                return fallback();
            }

            return (T)ResultFactory(request);
        }
    }
}
