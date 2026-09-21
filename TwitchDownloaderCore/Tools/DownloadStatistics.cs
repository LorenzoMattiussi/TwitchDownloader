using System.Diagnostics;

namespace TwitchDownloaderCore.Tools
{
    /// <summary>Accumulates byte/comment totals and elapsed time so a download can report its live throughput.</summary>
    internal sealed class DownloadStatistics
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private long _bytes;
        private long _comments;

        public long Bytes => Interlocked.Read(ref _bytes);
        public long Comments => Interlocked.Read(ref _comments);
        public TimeSpan Elapsed => _stopwatch.Elapsed;

        public void AddBytes(long bytes) => Interlocked.Add(ref _bytes, bytes);

        public void AddComments(long comments) => Interlocked.Add(ref _comments, comments);

        /// <summary>Returns a human readable throughput string, or null until enough data has been collected.</summary>
        public string GetSpeedText()
        {
            var seconds = _stopwatch.Elapsed.TotalSeconds;
            if (seconds < 0.25)
            {
                return null;
            }

            var mebibytesPerSecond = Bytes / 1024d / 1024d / seconds;
            var commentsPerSecond = Comments / seconds;
            return $"{mebibytesPerSecond:F2} MB/s, {commentsPerSecond:N0} comments/s";
        }
    }
}
