namespace TwitchDownloaderCore.Tools
{
    /// <summary>
    /// An async gate that limits how many operations may run at once. Unlike a fixed <see cref="SemaphoreSlim"/>
    /// the limit adapts to observed back-pressure: it shrinks immediately when the caller reports being throttled
    /// and grows back slowly after sustained success, so bursty callers self-tune instead of hammering a rate limit.
    /// </summary>
    internal sealed class AdaptiveConcurrencyLimiter
    {
        private readonly object _sync = new();
        private readonly LinkedList<Waiter> _waiters = new();
        private readonly int _maxLimit;
        private readonly int _minLimit;
        private readonly int _rampUpSuccesses;

        private int _limit;
        private int _inFlight;
        private int _successStreak;
        private long _resumeAtTicks;
        private Timer _cooldownTimer;

        public AdaptiveConcurrencyLimiter(int maxLimit, int minLimit = 1, int initialLimit = 1, int rampUpSuccesses = 20)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxLimit, 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(minLimit, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(minLimit, maxLimit);
            ArgumentOutOfRangeException.ThrowIfLessThan(initialLimit, minLimit);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(initialLimit, maxLimit);
            ArgumentOutOfRangeException.ThrowIfLessThan(rampUpSuccesses, 1);

            _maxLimit = maxLimit;
            _minLimit = minLimit;
            _limit = initialLimit;
            _rampUpSuccesses = rampUpSuccesses;
        }

        public int Limit { get { lock (_sync) { return _limit; } } }
        public int InFlight { get { lock (_sync) { return _inFlight; } } }
        public int Waiting { get { lock (_sync) { return _waiters.Count; } } }

        public async ValueTask<IDisposable> AcquireAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Waiter waiter;
            lock (_sync)
            {
                if (CanStart(DateTime.UtcNow.Ticks))
                {
                    _inFlight++;
                    return new Releaser(this);
                }

                waiter = new Waiter(cancellationToken);
                waiter.Node = _waiters.AddLast(waiter);
            }

            try
            {
                await waiter.Tcs.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                var releaseSlot = false;
                lock (_sync)
                {
                    if (waiter.Granted)
                    {
                        releaseSlot = true;
                    }
                    else if (waiter.Node is not null)
                    {
                        _waiters.Remove(waiter.Node);
                        waiter.Node = null;
                    }
                }

                waiter.Dispose();

                if (releaseSlot)
                {
                    Release();
                }

                throw;
            }

            waiter.Dispose();
            return new Releaser(this);
        }

        /// <summary>Records a successful operation, slowly allowing more concurrency.</summary>
        public void ReportSuccess()
        {
            lock (_sync)
            {
                if (_limit >= _maxLimit)
                {
                    return;
                }

                if (++_successStreak < _rampUpSuccesses)
                {
                    return;
                }

                _successStreak = 0;
                _limit++;
                Pump(DateTime.UtcNow.Ticks);
            }
        }

        /// <summary>Records throttling (e.g. HTTP 429), shrinking concurrency and pausing all acquisitions.</summary>
        public void ReportThrottled(TimeSpan? retryAfter)
        {
            lock (_sync)
            {
                _successStreak = 0;
                _limit = Math.Max(_minLimit, _limit / 2);

                var cooldown = retryAfter ?? TimeSpan.Zero;
                if (cooldown < TimeSpan.FromSeconds(1))
                {
                    cooldown = TimeSpan.FromSeconds(1);
                }
                else if (cooldown > TimeSpan.FromMinutes(5))
                {
                    cooldown = TimeSpan.FromMinutes(5);
                }

                var resumeAt = DateTime.UtcNow.Add(cooldown).Ticks;
                if (resumeAt <= _resumeAtTicks)
                {
                    return;
                }

                _resumeAtTicks = resumeAt;
                ScheduleCooldown(resumeAt);
            }
        }

        private bool CanStart(long nowTicks)
            => _inFlight < _limit && (_resumeAtTicks == 0 || nowTicks >= _resumeAtTicks);

        private void Pump(long nowTicks)
        {
            while (_waiters.First is { } firstNode)
            {
                if (!CanStart(nowTicks))
                {
                    break;
                }

                _waiters.RemoveFirst();
                var waiter = firstNode.Value;
                waiter.Node = null;

                if (waiter.Tcs.Task.IsCompleted)
                {
                    continue; // canceled while waiting
                }

                waiter.Granted = true;
                _inFlight++;
                waiter.Tcs.TrySetResult(true);
            }

            if (_waiters.Count > 0 && _resumeAtTicks > nowTicks)
            {
                ScheduleCooldown(_resumeAtTicks);
            }
        }

        private void ScheduleCooldown(long resumeAtTicks)
        {
            var delay = new DateTime(resumeAtTicks, DateTimeKind.Utc) - DateTime.UtcNow;
            if (delay < TimeSpan.Zero)
            {
                delay = TimeSpan.Zero;
            }

            _cooldownTimer?.Dispose();
            _cooldownTimer = new Timer(static state => ((AdaptiveConcurrencyLimiter)state).OnCooldownElapsed(), this, delay, Timeout.InfiniteTimeSpan);
        }

        private void OnCooldownElapsed()
        {
            lock (_sync)
            {
                if (DateTime.UtcNow.Ticks < _resumeAtTicks)
                {
                    ScheduleCooldown(_resumeAtTicks);
                    return;
                }

                _resumeAtTicks = 0;
                Pump(DateTime.UtcNow.Ticks);
            }
        }

        private void Release()
        {
            lock (_sync)
            {
                if (_inFlight > 0)
                {
                    _inFlight--;
                }

                Pump(DateTime.UtcNow.Ticks);
            }
        }

        private sealed class Waiter : IDisposable
        {
            public readonly TaskCompletionSource<bool> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public LinkedListNode<Waiter> Node;
            public bool Granted;
            private readonly CancellationTokenRegistration _registration;

            public Waiter(CancellationToken cancellationToken)
            {
                if (cancellationToken.CanBeCanceled)
                {
                    _registration = cancellationToken.Register(static state => ((Waiter)state).Tcs.TrySetCanceled(), this);
                }
            }

            public void Dispose() => _registration.Dispose();
        }

        private sealed class Releaser : IDisposable
        {
            private AdaptiveConcurrencyLimiter _owner;

            public Releaser(AdaptiveConcurrencyLimiter owner) => _owner = owner;

            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
        }
    }
}
