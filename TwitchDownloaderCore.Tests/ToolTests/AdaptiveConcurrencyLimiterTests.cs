using System.Diagnostics;
using TwitchDownloaderCore.Tools;

namespace TwitchDownloaderCore.Tests.ToolTests
{
    public class AdaptiveConcurrencyLimiterTests
    {
        [Fact]
        public async Task NeverExceedsTheLimit()
        {
            var limiter = new AdaptiveConcurrencyLimiter(maxLimit: 2, minLimit: 1, initialLimit: 2, rampUpSuccesses: 1000);

            var first = await limiter.AcquireAsync(CancellationToken.None);
            var second = await limiter.AcquireAsync(CancellationToken.None);

            Assert.Equal(2, limiter.InFlight);

            var third = limiter.AcquireAsync(CancellationToken.None).AsTask();
            var completed = await Task.WhenAny(third, Task.Delay(250, TestContext.Current.CancellationToken));
            Assert.NotSame(third, completed);
            Assert.Equal(1, limiter.Waiting);

            first.Dispose();

            var thirdLease = await third.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(2, limiter.InFlight);

            second.Dispose();
            thirdLease.Dispose();
            Assert.Equal(0, limiter.InFlight);
        }

        [Fact]
        public void ReportSuccessSlowlyRaisesTheLimit()
        {
            var limiter = new AdaptiveConcurrencyLimiter(maxLimit: 3, minLimit: 1, initialLimit: 1, rampUpSuccesses: 2);

            Assert.Equal(1, limiter.Limit);

            limiter.ReportSuccess();
            limiter.ReportSuccess();
            Assert.Equal(2, limiter.Limit);

            limiter.ReportSuccess();
            limiter.ReportSuccess();
            Assert.Equal(3, limiter.Limit);

            limiter.ReportSuccess();
            limiter.ReportSuccess();
            Assert.Equal(3, limiter.Limit); // Capped at the maximum
        }

        [Fact]
        public void ReportThrottledHalvesTheLimitDownToTheMinimum()
        {
            var limiter = new AdaptiveConcurrencyLimiter(maxLimit: 8, minLimit: 1, initialLimit: 8);

            limiter.ReportThrottled(TimeSpan.FromMilliseconds(1));
            Assert.Equal(4, limiter.Limit);

            limiter.ReportThrottled(TimeSpan.FromMilliseconds(1));
            Assert.Equal(2, limiter.Limit);

            limiter.ReportThrottled(TimeSpan.FromMilliseconds(1));
            Assert.Equal(1, limiter.Limit);

            limiter.ReportThrottled(TimeSpan.FromMilliseconds(1));
            Assert.Equal(1, limiter.Limit); // Never below the minimum
        }

        [Fact]
        public async Task ReportThrottledPausesNewAcquisitions()
        {
            var limiter = new AdaptiveConcurrencyLimiter(maxLimit: 2, minLimit: 1, initialLimit: 2);

            limiter.ReportThrottled(TimeSpan.FromSeconds(1));

            var stopwatch = Stopwatch.StartNew();
            using var lease = await limiter.AcquireAsync(CancellationToken.None);
            stopwatch.Stop();

            Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(800), $"Acquired a slot after only {stopwatch.Elapsed.TotalMilliseconds:F0}ms while throttled.");
        }

        [Fact]
        public async Task CanceledWaiterDoesNotLeakASlot()
        {
            var limiter = new AdaptiveConcurrencyLimiter(maxLimit: 1, minLimit: 1, initialLimit: 1);

            var held = await limiter.AcquireAsync(CancellationToken.None);

            using var cts = new CancellationTokenSource();
            var pending = limiter.AcquireAsync(cts.Token).AsTask();
            Assert.Equal(1, limiter.Waiting);

            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal(0, limiter.Waiting);

            held.Dispose();

            using var after = await limiter.AcquireAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(1, limiter.InFlight);
        }
    }
}
