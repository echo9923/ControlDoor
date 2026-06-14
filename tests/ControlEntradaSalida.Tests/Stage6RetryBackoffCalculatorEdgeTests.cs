using System;
using ControlDoor.Configuration;
using ControlDoor.Permissions;

namespace ControlEntradaSalida.Tests
{
    public static class Stage6RetryBackoffCalculatorEdgeTests
    {
        [TestCase]
        public static void RetryBackoffCalculator_NonPositiveAttempt_ClampsToOne()
        {
            var calculator = new RetryBackoffCalculator(new DeviceOperationRetryOptions
            {
                InitialRetryDelaySeconds = 60,
                MaxRetryDelaySeconds = 300
            });

            Assert.Equal(calculator.CalculateDelay(1), calculator.CalculateDelay(0));
            Assert.Equal(calculator.CalculateDelay(1), calculator.CalculateDelay(-5));
        }

        [TestCase]
        public static void RetryBackoffCalculator_InitialDelayBelowOne_FallsBackToSixty()
        {
            var calculator = new RetryBackoffCalculator(new DeviceOperationRetryOptions
            {
                InitialRetryDelaySeconds = 0,
                MaxRetryDelaySeconds = 300
            });

            Assert.Equal(TimeSpan.FromSeconds(60), calculator.CalculateDelay(1));
        }

        [TestCase]
        public static void RetryBackoffCalculator_MaxDelayBelowInitial_FallsBackTo3600()
        {
            var calculator = new RetryBackoffCalculator(new DeviceOperationRetryOptions
            {
                InitialRetryDelaySeconds = 60,
                MaxRetryDelaySeconds = 10
            });

            Assert.Equal(TimeSpan.FromSeconds(60), calculator.CalculateDelay(1));
            Assert.Equal(TimeSpan.FromSeconds(3600), calculator.CalculateDelay(20));
        }

        [TestCase]
        public static void RetryBackoffCalculator_LargeAttempt_CappedAtMax()
        {
            var calculator = new RetryBackoffCalculator(new DeviceOperationRetryOptions
            {
                InitialRetryDelaySeconds = 60,
                MaxRetryDelaySeconds = 300
            });

            Assert.Equal(TimeSpan.FromSeconds(300), calculator.CalculateDelay(30));
            Assert.Equal(TimeSpan.FromSeconds(300), calculator.CalculateDelay(100));
        }

        [TestCase]
        public static void RetryBackoffCalculator_NullOptions_UsesDefaults()
        {
            var calculator = new RetryBackoffCalculator(null);

            Assert.Equal(TimeSpan.FromSeconds(60), calculator.CalculateDelay(1));
            Assert.Equal(TimeSpan.FromSeconds(120), calculator.CalculateDelay(2));
        }

        [TestCase]
        public static void RetryBackoffCalculator_CalculateNextRetryAt_AddsDelayToNow()
        {
            var now = new DateTime(2026, 6, 13, 8, 0, 0);
            var calculator = new RetryBackoffCalculator(new DeviceOperationRetryOptions
            {
                InitialRetryDelaySeconds = 60,
                MaxRetryDelaySeconds = 300
            });

            Assert.Equal(now.AddSeconds(60), calculator.CalculateNextRetryAt(now, 1));
            Assert.Equal(now.AddSeconds(120), calculator.CalculateNextRetryAt(now, 2));
            Assert.Equal(now.AddSeconds(240), calculator.CalculateNextRetryAt(now, 3));
        }
    }
}
