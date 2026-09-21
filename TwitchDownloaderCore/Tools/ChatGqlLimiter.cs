namespace TwitchDownloaderCore.Tools
{
    /// <summary>
    /// Process-wide concurrency limiter for the Twitch GQL requests made while downloading chat (comment
    /// pages and commenter backfill). It is shared by every concurrent chat download so that running many
    /// downloads at once cannot collectively flood the API, and it backs off globally when Twitch throttles us.
    /// </summary>
    internal static class ChatGqlLimiter
    {
        private const int MaxConcurrency = 12;
        private const int MinConcurrency = 2;
        private const int InitialConcurrency = 4;

        private static readonly AdaptiveConcurrencyLimiter Limiter = new(MaxConcurrency, MinConcurrency, InitialConcurrency);

        public static ValueTask<IDisposable> AcquireAsync(CancellationToken cancellationToken) => Limiter.AcquireAsync(cancellationToken);

        public static void ReportSuccess() => Limiter.ReportSuccess();

        public static void ReportThrottled(TimeSpan? retryAfter) => Limiter.ReportThrottled(retryAfter);
    }
}
