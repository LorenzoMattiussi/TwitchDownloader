namespace TwitchDownloaderCore.Tests.DownloaderTests
{
    public class DownloadRangeTests
    {
        [Theory]
        [InlineData(0, 0, 1)]
        [InlineData(0, 1, 4)]
        [InlineData(0, 60, 1)]
        [InlineData(0, 600, 4)]
        [InlineData(0, 3600, 7)]
        [InlineData(0, 14400, 4)]
        [InlineData(0, 86400, 4)]
        [InlineData(100, 220, 4)]
        [InlineData(1000, 101000, 50)]
        public void RangesTileTheDownloadRangeExactly(int videoStart, int videoEnd, int connectionCount)
        {
            var ranges = ChatDownloader.GenerateDownloadRanges(videoStart, videoEnd, connectionCount);

            Assert.NotEmpty(ranges);

            // Ranges must be ordered, contiguous, non-inverted, and cover [videoStart, videoEnd) exactly.
            var expectedStart = videoStart;
            foreach (var range in ranges)
            {
                Assert.Equal(expectedStart, range.Start.Value);
                Assert.True(range.End.Value >= range.Start.Value, $"Range [{range.Start.Value}, {range.End.Value}) is inverted.");
                expectedStart = range.End.Value;
            }

            Assert.Equal(videoEnd, expectedStart);
        }

        [Theory]
        [InlineData(0, 60, 4, 4)]        // Limited to MinChunkSeconds
        [InlineData(0, 600, 4, 32)]      // 8 chunks per worker
        [InlineData(0, 14400, 4, 32)]
        [InlineData(0, 86400, 4, 32)]
        [InlineData(0, 600, 1, 1)]       // A single worker gets a single range
        [InlineData(0, 3600, 1, 1)]
        public void OversamplesButStaysBounded(int videoStart, int videoEnd, int connectionCount, int expectedChunkCount)
        {
            var ranges = ChatDownloader.GenerateDownloadRanges(videoStart, videoEnd, connectionCount);

            Assert.Equal(expectedChunkCount, ranges.Count);
        }

        [Fact]
        public void SingleWorkerProducesASingleFullRange()
        {
            var ranges = ChatDownloader.GenerateDownloadRanges(100, 700, 1);

            var range = Assert.Single(ranges);
            Assert.Equal(100, range.Start.Value);
            Assert.Equal(700, range.End.Value);
        }
    }
}
