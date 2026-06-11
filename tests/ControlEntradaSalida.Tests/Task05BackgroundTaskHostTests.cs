using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ControlDoor.Runtime;

namespace ControlEntradaSalida.Tests
{
    public static class Task05BackgroundTaskHostTests
    {
        [TestCase]
        public static void BackgroundTaskHost_StartsAndStopsByConfiguredOrder()
        {
            var events = new List<string>();
            using (var host = new BackgroundTaskHost())
            {
                host.Register(new FakeBackgroundTask("second", events), startOrder: 2, stopOrder: 1);
                host.Register(new FakeBackgroundTask("first", events), startOrder: 1, stopOrder: 2);

                var result = host.StartAsync().GetAwaiter().GetResult();
                host.StopAsync().GetAwaiter().GetResult();

                Assert.True(result.Success);
                Assert.Equal("start:first", events[0]);
                Assert.Equal("start:second", events[1]);
                Assert.Equal("stop:second", events[2]);
                Assert.Equal("stop:first", events[3]);
            }
        }

        [TestCase]
        public static void BackgroundTaskHost_StopPassesCancellationToken()
        {
            var events = new List<string>();
            var task = new FakeBackgroundTask("worker", events);
            using (var host = new BackgroundTaskHost())
            {
                host.Register(task, startOrder: 0, stopOrder: 0);
                host.StartAsync().GetAwaiter().GetResult();
                host.StopAsync().GetAwaiter().GetResult();
            }

            Assert.True(task.StopSawCancellation);
        }

        [TestCase]
        public static void BackgroundTaskHost_NonCriticalStartFailure_IsPartialSuccess()
        {
            var events = new List<string>();
            using (var host = new BackgroundTaskHost())
            {
                host.Register(new FakeBackgroundTask("bad", events, failOnStart: true), startOrder: 0, stopOrder: 0);
                host.Register(new FakeBackgroundTask("good", events), startOrder: 1, stopOrder: 1);

                var result = host.StartAsync().GetAwaiter().GetResult();

                Assert.True(result.Success);
                Assert.True(result.PartialSuccess);
                Assert.Equal("start:good", events[1]);
            }
        }

        [TestCase]
        public static void BackgroundTaskHost_CriticalStartFailure_FailsHost()
        {
            var events = new List<string>();
            using (var host = new BackgroundTaskHost())
            {
                host.Register(new FakeBackgroundTask("bad", events, isCritical: true, failOnStart: true), startOrder: 0, stopOrder: 0);
                host.Register(new FakeBackgroundTask("never", events), startOrder: 1, stopOrder: 1);

                var result = host.StartAsync().GetAwaiter().GetResult();

                Assert.False(result.Success);
                Assert.Equal(1, events.Count);
            }
        }

        [TestCase]
        public static void BackgroundTaskHost_StopTimeoutMarksStatus()
        {
            var events = new List<string>();
            using (var host = new BackgroundTaskHost())
            {
                host.Register(new FakeBackgroundTask("slow", events, stopDelay: TimeSpan.FromMilliseconds(150)), startOrder: 0, stopOrder: 0);
                host.StartAsync().GetAwaiter().GetResult();
                host.StopAsync(TimeSpan.FromMilliseconds(10)).GetAwaiter().GetResult();

                var status = host.GetStatuses()[0];
                Assert.True(status.StopTimedOut);
            }
        }

        [TestCase]
        public static void DelayScheduler_RunsActionAfterDelay()
        {
            var scheduler = new DelayScheduler();
            var ran = false;

            scheduler.ScheduleAsync(TimeSpan.FromMilliseconds(1), token =>
            {
                ran = true;
                return Task.CompletedTask;
            }, CancellationToken.None).GetAwaiter().GetResult();

            Assert.True(ran);
        }
    }
}
