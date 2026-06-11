using System;
using System.Threading;
using System.Threading.Tasks;

namespace ControlDoor.Runtime
{
    public sealed class DelayScheduler
    {
        public Task ScheduleAsync(TimeSpan delay, Func<CancellationToken, Task> action, CancellationToken cancellationToken)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (delay < TimeSpan.Zero)
            {
                delay = TimeSpan.Zero;
            }

            return Task.Run(async () =>
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                await action(cancellationToken).ConfigureAwait(false);
            }, cancellationToken);
        }
    }
}
