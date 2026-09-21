using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using TwitchDownloaderCore.Chat;
using TwitchDownloaderCore.Interfaces;
using TwitchDownloaderCore.Models;
using TwitchDownloaderCore.Options;
using TwitchDownloaderCore.Services;
using TwitchDownloaderCore.Tools;
using TwitchDownloaderCore.TwitchObjects;
using TwitchDownloaderCore.TwitchObjects.Gql;

namespace TwitchDownloaderCore
{
    public sealed class ChatDownloader
    {
        private readonly ChatDownloadOptions downloadOptions;
        private readonly ITaskProgress _progress;
        private readonly string _cacheDir;

        private static readonly HttpClient HttpClient = new(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            EnableMultipleHttp2Connections = true
        })
        {
            BaseAddress = new Uri("https://gql.twitch.tv/gql"),
            DefaultRequestHeaders = { { "Client-ID", "kd1unb4b3q4t58fwlpcbzcbnm76a8fp" } },
            // GQL is latency-bound and paginated; HTTP/2 multiplexes the many parallel page requests
            // instead of paying for a new HTTP/1.1 connection per request.
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
        };

        private enum DownloadType
        {
            Clip,
            Video
        }

        public ChatDownloader(ChatDownloadOptions chatDownloadOptions, ITaskProgress progress)
        {
            downloadOptions = chatDownloadOptions;
            _progress = progress;
            _cacheDir = CacheDirectoryService.GetCacheDirectory(downloadOptions.TempFolder);
        }

        private async Task<List<Comment>> DownloadSection(Range downloadRange, string videoId, DateTime videoCreatedAt, bool runToEnd, IProgress<int> downloadProgress, CancellationToken cancellationToken, DownloadStatistics statistics = null)
        {
            var comments = new List<Comment>();
            int videoStart = downloadRange.Start.Value;
            int videoEnd = downloadRange.End.Value;
            int videoDuration = videoEnd - videoStart;
            double latestMessage = videoStart - 1;
            bool isFirst = true;
            string cursor = "";
            double errorCount = 0;
            double nullCount = 0;

            while (runToEnd || latestMessage < videoEnd)
            {
                cancellationToken.ThrowIfCancellationRequested();

                GqlCommentResponse commentResponse;
                try
                {
                    var request = new HttpRequestMessage()
                    {
                        Method = HttpMethod.Post
                    };

                    if (isFirst)
                    {
                        request.Content = new StringContent(
                            "{\"operationName\":\"VideoCommentsByOffsetOrCursor\",\"variables\":{\"videoID\":\"" + videoId + "\",\"contentOffsetSeconds\":" + videoStart + "},\"extensions\":{\"persistedQuery\":{\"version\":1,\"sha256Hash\":\"b70a3591ff0f4e0313d126c6a1502d79a1c02baebb288227c582044aa76adf6a\"}}}",
                            Encoding.UTF8, "application/json");
                    }
                    else
                    {
                        request.Content = new StringContent(
                            "{\"operationName\":\"VideoCommentsByOffsetOrCursor\",\"variables\":{\"videoID\":\"" + videoId + "\",\"cursor\":\"" + cursor + "\"},\"extensions\":{\"persistedQuery\":{\"version\":1,\"sha256Hash\":\"b70a3591ff0f4e0313d126c6a1502d79a1c02baebb288227c582044aa76adf6a\"}}}",
                            Encoding.UTF8, "application/json");
                    }

                    // A single shared limiter keeps every concurrent chat download within Twitch's tolerance
                    using (await ChatGqlLimiter.AcquireAsync(cancellationToken).ConfigureAwait(false))
                    using (var httpResponse = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                    {
                        if (httpResponse.StatusCode == HttpStatusCode.TooManyRequests)
                        {
                            ChatGqlLimiter.ReportThrottled(GetRetryAfter(httpResponse));
                            throw new HttpRequestException("Twitch rate limit reached", null, HttpStatusCode.TooManyRequests);
                        }

                        httpResponse.EnsureSuccessStatusCode();

                        var responseBytes = await httpResponse.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                        statistics?.AddBytes(responseBytes.Length);
                        commentResponse = JsonSerializer.Deserialize<GqlCommentResponse>(responseBytes, ChatSerialization.WebOptions);
                        ChatGqlLimiter.ReportSuccess();
                    }
                }
                catch (HttpRequestException ex)
                {
                    if (++errorCount > 10)
                    {
                        throw;
                    }

                    _progress.LogVerbose($"Exception '{ex.Message}' thrown at {latestMessage}s ({cursor}) in range {downloadRange}. Current error factor: {errorCount}.");
                    await Task.Delay((int)(1_000 * errorCount), cancellationToken);
                    continue;
                }

                // video.comments can be null for some dumb reason
                if (commentResponse.data.video.comments?.edges is null)
                {
                    if (++nullCount > 10)
                    {
                        throw new Exception("Received too many null comment lists. Try reducing your download threads.");
                    }

                    _progress.LogVerbose($"Received null comment list at {latestMessage}s ({cursor}) in range {downloadRange}. Current null factor: {nullCount}.");
                    await Task.Delay((int)(100 * nullCount), cancellationToken);
                    continue;
                }

                const double BACK_OFF_FACTOR = 0.1;
                nullCount = Math.Max(0, nullCount - BACK_OFF_FACTOR);
                errorCount = Math.Max(0, errorCount - BACK_OFF_FACTOR);

                var convertedComments = ConvertComments(commentResponse.data.video, videoCreatedAt);
                statistics?.AddComments(convertedComments.Count);
                foreach (var comment in convertedComments)
                {
                    if (comment.content_offset_seconds >= videoStart && (runToEnd || comment.content_offset_seconds < videoEnd))
                    {
                        comments.Add(comment);
                    }

                    if (comment.content_offset_seconds > latestMessage)
                    {
                        latestMessage = comment.content_offset_seconds;
                    }
                }

                if (!commentResponse.data.video.comments.pageInfo.hasNextPage)
                    break;

                cursor = commentResponse.data.video.comments.edges.Last().cursor;

                var percent = (int)Math.Floor((latestMessage - videoStart) / videoDuration * 100);
                if (runToEnd)
                    percent = Math.Min(percent, 99);

                downloadProgress.Report(percent);

                if (isFirst)
                {
                    isFirst = false;
                }
            }

            return comments;
        }

        private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
        {
            var retryAfter = response.Headers.RetryAfter;
            if (retryAfter is null)
            {
                return null;
            }

            if (retryAfter.Delta is { } delta)
            {
                return delta;
            }

            if (retryAfter.Date is { } date)
            {
                var remaining = date - DateTimeOffset.UtcNow;
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }

            return null;
        }

        private List<Comment> ConvertComments(CommentVideo video, DateTime videoCreatedAt)
        {
            List<Comment> returnList = new List<Comment>(video.comments.edges.Count);

            foreach (var comment in video.comments.edges)
            {
                //Commenter can be null for some reason, skip (deleted account?)
                if (comment.node.commenter == null)
                    continue;

                var oldComment = comment.node;

                // As of April 2025, Twitch returns comment.createdAt as local time but uses UTC formatting in the request
                var newCreatedAt = oldComment.createdAt - videoCreatedAt < TimeSpan.FromMinutes(-5) // Sometimes Twitch takes a few minutes to register the creation of a video
                    ? DateTime.SpecifyKind(oldComment.createdAt, DateTimeKind.Local).ToUniversalTime()
                    : oldComment.createdAt;

                var newComment = new Comment
                {
                    _id = oldComment.id,
                    created_at = newCreatedAt,
                    channel_id = video.creator?.id ?? "", // Deliberate empty string for ChatJson.UpgradeChatJson
                    content_type = "video",
                    content_id = video.id,
                    content_offset_seconds = oldComment.contentOffsetSeconds,
                    commenter = new Commenter
                    {
                        display_name = oldComment.commenter.displayName.Trim(),
                        _id = oldComment.commenter.id,
                        name = oldComment.commenter.login
                    }
                };
                var message = new Message();

                const int AVERAGE_WORD_LENGTH = 5; // The average english word is ~4.7 chars. Round up to partially account for spaces
                var bodyStringBuilder = new StringBuilder(oldComment.message.fragments.Count * AVERAGE_WORD_LENGTH);
                if (downloadOptions.DownloadFormat == ChatFormat.Text)
                {
                    // Optimize allocations for writing text chats
                    foreach (var fragment in oldComment.message.fragments)
                    {
                        if (fragment.text == null)
                            continue;

                        bodyStringBuilder.Append(fragment.text);
                    }
                }
                else
                {
                    var fragments = new List<Fragment>(oldComment.message.fragments.Count);
                    var emoticons = new List<Emoticon2>();
                    foreach (var fragment in oldComment.message.fragments)
                    {
                        if (fragment.text == null)
                            continue;

                        bodyStringBuilder.Append(fragment.text);

                        var newFragment = new Fragment
                        {
                            text = fragment.text
                        };
                        if (fragment.emote != null)
                        {
                            newFragment.emoticon = new Emoticon
                            {
                                emoticon_id = fragment.emote.emoteID
                            };

                            var newEmote = new Emoticon2
                            {
                                _id = fragment.emote.emoteID,
                                begin = fragment.emote.from
                            };
                            newEmote.end = newEmote.begin + fragment.text.Length + 1;
                            emoticons.Add(newEmote);
                        }

                        fragments.Add(newFragment);
                    }

                    message.fragments = fragments;
                    message.emoticons = emoticons;
                    var badges = new List<UserBadge>(oldComment.message.userBadges.Count);
                    foreach (var badge in oldComment.message.userBadges)
                    {
                        if (string.IsNullOrEmpty(badge.setID) && string.IsNullOrEmpty(badge.version))
                            continue;

                        var newBadge = new UserBadge
                        {
                            _id = badge.setID,
                            version = badge.version
                        };
                        badges.Add(newBadge);
                    }

                    message.user_badges = badges;
                    message.user_color = oldComment.message.userColor;
                }

                message.body = bodyStringBuilder.ToString();

                // Cheer amounts start with a non-zero digit, so a cheap digit scan skips the regex for almost every comment
                if (message.body.AsSpan().IndexOfAny("123456789") >= 0)
                {
                    var bitMatch = TwitchRegex.BitsRegex.Match(message.body);
                    if (bitMatch.Success && int.TryParse(bitMatch.ValueSpan, out var result))
                    {
                        message.bits_spent = result;
                    }
                }

                newComment.message = message;

                returnList.Add(newComment);
            }

            return returnList;
        }

        public async Task DownloadAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(downloadOptions.Id))
            {
                throw new NullReferenceException("Null or empty video/clip ID");
            }

            var outputFileInfo = TwitchHelper.ClaimFile(downloadOptions.Filename, downloadOptions.FileCollisionCallback, _progress);
            downloadOptions.Filename = outputFileInfo.FullName;

            // Open the destination file so that it exists in the filesystem.
            await using var outputFs = outputFileInfo.Open(FileMode.Create, FileAccess.Write, FileShare.Read);

            try
            {
                await DownloadAsyncImpl(outputFileInfo, outputFs, cancellationToken);
            }
            catch
            {
                await Task.Delay(100, CancellationToken.None);

                TwitchHelper.CleanUpClaimedFile(outputFileInfo, outputFs, _progress);

                throw;
            }
        }

        private async Task DownloadAsyncImpl(FileInfo outputFileInfo, FileStream outputFs, CancellationToken cancellationToken)
        {
            DownloadType downloadType = downloadOptions.Id.All(char.IsDigit) ? DownloadType.Video : DownloadType.Clip;

            var (chatRoot, connectionCount, chaptersTask) = await InitChatRoot(downloadType);

            // Chapters come from a different endpoint and are not needed to download comments, so fetch both at once
            var commentsTask = DownloadComments(downloadType, chatRoot.video, connectionCount, cancellationToken);
            await Task.WhenAll(commentsTask, chaptersTask);

            chatRoot.comments = commentsTask.Result;

            // Sometimes the API returns a video length of 0. Assume the last comment is when the video ends
            if (chatRoot.video.length <= 0 && chatRoot.comments.LastOrDefault() is { } lastComment)
            {
                chatRoot.video.length = lastComment.content_offset_seconds;
                if (chatRoot.video.end <= 0)
                {
                    chatRoot.video.end = lastComment.content_offset_seconds;
                }
            }

            var embedRequested = downloadOptions.EmbedData && (downloadOptions.DownloadFormat is ChatFormat.Json or ChatFormat.Html);
            var backfillRequested = downloadOptions.DownloadFormat is ChatFormat.Json;

            if (embedRequested && backfillRequested)
            {
                // Both stages are network-bound and independent, so run them together behind one progress readout
                _progress.SetTemplateStatus("Finishing Up {0}%", 0);

                var stagePercents = new int[2];
                void ReportCombined() => _progress.ReportProgress((stagePercents[0] + stagePercents[1]) / 2);

                var embedTask = EmbedImages(chatRoot, cancellationToken, percent => { stagePercents[0] = percent; ReportCombined(); });
                var backfillTask = BackfillUserInfo(chatRoot, cancellationToken, percent => { stagePercents[1] = percent; ReportCombined(); });

                await Task.WhenAll(embedTask, backfillTask);
            }
            else if (embedRequested)
            {
                await EmbedImages(chatRoot, cancellationToken);
            }
            else if (backfillRequested)
            {
                await BackfillUserInfo(chatRoot, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            _progress.SetStatus("Writing Output File");

            Stream outputStream = downloadOptions.Compression switch
            {
                ChatCompression.None => outputFs,
                ChatCompression.Gzip => new GZipStream(outputFs, CompressionLevel.Optimal),
                _ => throw new NotSupportedException($"{downloadOptions.Compression} is not a supported chat compression.")
            };

            try
            {
                switch (downloadOptions.DownloadFormat)
                {
                    case ChatFormat.Json:
                        await ChatJson.SerializeAsync(outputStream, chatRoot, cancellationToken);
                        break;
                    case ChatFormat.Html:
                        await ChatHtml.SerializeAsync(outputStream, outputFileInfo.FullName, chatRoot, _cacheDir, _progress, downloadOptions.EmbedData, cancellationToken);
                        break;
                    case ChatFormat.Text:
                        await ChatText.SerializeAsync(outputStream, chatRoot, downloadOptions.TimeFormat);
                        break;
                    default:
                        throw new NotSupportedException($"{downloadOptions.DownloadFormat} is not a supported output format.");
                }
            }
            finally
            {
                if (outputStream is GZipStream gzipStream)
                {
                    // GZipStream finishes writing on disposal, not flush.
                    await gzipStream.DisposeAsync();
                }
            }
        }

        private async Task<(ChatRoot chatRoot, int connectionCount, Task chaptersTask)> InitChatRoot(DownloadType downloadType)
        {
            var chatRoot = new ChatRoot
            {
                FileInfo = new ChatRootInfo { Version = ChatRootVersion.CurrentVersion, CreatedAt = DateTime.Now },
                streamer = new Streamer(),
                video = new Video(),
                comments = new List<Comment>()
            };

            string videoId = downloadOptions.Id;
            int connectionCount;
            Task chaptersTask;

            if (downloadType == DownloadType.Video)
            {
                GqlVideoResponse videoInfoResponse = await TwitchHelper.GetVideoInfo(long.Parse(videoId));
                if (videoInfoResponse.data.video == null)
                {
                    throw new NullReferenceException("Invalid VOD, deleted/expired VOD possibly?");
                }

                chatRoot.streamer.name = videoInfoResponse.data.video.owner?.displayName;
                chatRoot.streamer.login = videoInfoResponse.data.video.owner?.login;
                chatRoot.streamer.id = int.Parse(videoInfoResponse.data.video.owner?.id ?? "0");
                chatRoot.video.description = videoInfoResponse.data.video.description?.Replace("  \n", "\n").Replace("\n\n", "\n").TrimEnd();
                chatRoot.video.title = videoInfoResponse.data.video.title;
                chatRoot.video.created_at = videoInfoResponse.data.video.createdAt;
                chatRoot.video.start = downloadOptions.TrimBeginning ? Math.Max(0, downloadOptions.TrimBeginningTime) : 0.0;
                chatRoot.video.end = downloadOptions.TrimEnding ? Math.Min(downloadOptions.TrimEndingTime, videoInfoResponse.data.video.lengthSeconds) : videoInfoResponse.data.video.lengthSeconds;
                chatRoot.video.length = videoInfoResponse.data.video.lengthSeconds;
                chatRoot.video.viewCount = videoInfoResponse.data.video.viewCount;
                chatRoot.video.game = videoInfoResponse.data.video.game?.displayName ?? "Unknown";
                var downloadLength = chatRoot.video.end - chatRoot.video.start;
                connectionCount = downloadLength / downloadOptions.DownloadThreads < 1
                    ? Math.Max((int)downloadLength, 1)
                    : downloadOptions.DownloadThreads;

                chaptersTask = PopulateVideoChaptersAsync(chatRoot, videoId, videoInfoResponse.data.video);
            }
            else
            {
                var clipInfoResponse = await TwitchHelper.GetShareClipRenderStatus(videoId);
                if (clipInfoResponse.data.clip.video == null || clipInfoResponse.data.clip.videoOffsetSeconds == null)
                {
                    throw new NullReferenceException("Invalid VOD for clip, deleted/expired VOD possibly?");
                }

                videoId = clipInfoResponse.data.clip.video.id;
                chatRoot.streamer.name = clipInfoResponse.data.clip.broadcaster?.displayName;
                chatRoot.streamer.login = clipInfoResponse.data.clip.broadcaster?.login;
                chatRoot.streamer.id = int.Parse(clipInfoResponse.data.clip.broadcaster?.id ?? "0");
                chatRoot.clipper = new Clipper
                {
                    name = clipInfoResponse.data.clip.curator?.displayName,
                    login = clipInfoResponse.data.clip.curator?.login,
                    id = int.Parse(clipInfoResponse.data.clip.curator?.id ?? "0"),
                };
                chatRoot.video.title = clipInfoResponse.data.clip.title;
                chatRoot.video.created_at = clipInfoResponse.data.clip.createdAt;
                chatRoot.video.start = (double)clipInfoResponse.data.clip.videoOffsetSeconds + (downloadOptions.TrimBeginning ? downloadOptions.TrimBeginningTime : 0);
                chatRoot.video.end = (double)clipInfoResponse.data.clip.videoOffsetSeconds + (downloadOptions.TrimEnding ? downloadOptions.TrimEndingTime : clipInfoResponse.data.clip.durationSeconds);
                chatRoot.video.length = clipInfoResponse.data.clip.durationSeconds;
                chatRoot.video.viewCount = clipInfoResponse.data.clip.viewCount;
                chatRoot.video.game = clipInfoResponse.data.clip.game?.displayName ?? "Unknown";
                connectionCount = 1;

                var clipChapter = TwitchHelper.GenerateClipChapter(clipInfoResponse.data.clip);
                chatRoot.video.chapters.Add(new VideoChapter
                {
                    id = clipChapter.node.id,
                    startMilliseconds = clipChapter.node.positionMilliseconds,
                    lengthMilliseconds = clipChapter.node.durationMilliseconds,
                    _type = clipChapter.node._type,
                    description = clipChapter.node.description,
                    subDescription = clipChapter.node.subDescription,
                    thumbnailUrl = clipChapter.node.thumbnailURL,
                    gameId = clipChapter.node.details.game?.id,
                    gameDisplayName = clipChapter.node.details.game?.displayName,
                    gameBoxArtUrl = clipChapter.node.details.game?.boxArtURL
                });

                chaptersTask = Task.CompletedTask;
            }

            chatRoot.video.id = videoId;

            return (chatRoot, connectionCount, chaptersTask);
        }

        private async Task PopulateVideoChaptersAsync(ChatRoot chatRoot, string videoId, VideoInfo videoInfo)
        {
            GqlVideoChapterResponse videoChapterResponse = await TwitchHelper.GetOrGenerateVideoChapters(long.Parse(videoId), videoInfo, _progress);
            chatRoot.video.chapters.EnsureCapacity(videoChapterResponse.data.video.moments.edges.Count);
            foreach (var responseChapter in videoChapterResponse.data.video.moments.edges)
            {
                chatRoot.video.chapters.Add(new VideoChapter
                {
                    id = responseChapter.node.id,
                    startMilliseconds = responseChapter.node.positionMilliseconds,
                    lengthMilliseconds = responseChapter.node.durationMilliseconds,
                    _type = responseChapter.node._type,
                    description = responseChapter.node.description,
                    subDescription = responseChapter.node.subDescription,
                    thumbnailUrl = responseChapter.node.thumbnailURL,
                    gameId = responseChapter.node.details.game?.id,
                    gameDisplayName = responseChapter.node.details.game?.displayName,
                    gameBoxArtUrl = responseChapter.node.details.game?.boxArtURL
                });
            }
        }

        private async Task<List<Comment>> DownloadComments(DownloadType downloadType, Video video, int connectionCount, CancellationToken cancellationToken)
        {
            _progress.SetTemplateStatus("Downloading {0}%", 0);

            connectionCount = Math.Max(1, connectionCount);

            var videoStart = (int)Math.Floor(video.start);
            var videoEnd = (int)Math.Ceiling(video.end) + 1; // Exclusive end

            var downloadRanges = GenerateDownloadRanges(videoStart, videoEnd, connectionCount);
            var chunkCount = downloadRanges.Count;

            var percentages = new int[chunkCount];
            var chunkResults = new List<Comment>[chunkCount];
            var statistics = new DownloadStatistics();

            await Parallel.ForEachAsync(Enumerable.Range(0, chunkCount), new ParallelOptions
            {
                MaxDegreeOfParallelism = connectionCount,
                CancellationToken = cancellationToken
            }, async (i, token) =>
            {
                // The final range runs to the true end of the video to catch the tail
                var runToEnd = downloadType is DownloadType.Video && !downloadOptions.TrimEnding && i == chunkCount - 1;

                var taskProgress = new Progress<int>(percent =>
                {
                    percentages[i] = Math.Clamp(percent, 0, 100);
                    _progress.ReportProgress(percentages.Sum() / chunkCount, statistics.GetSpeedText());
                });

                chunkResults[i] = await DownloadSection(downloadRanges[i], video.id, video.created_at, runToEnd, taskProgress, token, statistics);
            });

            _progress.ReportProgress(100);

            if (statistics.GetSpeedText() is { } speedText)
            {
                _progress.LogInfo($"Downloaded {statistics.Comments:N0} comments ({statistics.Bytes / 1024d / 1024d:F1} MB) in {statistics.Elapsed.TotalSeconds:F1}s ({speedText}).");
            }

            var commentList = chunkResults
                .SelectMany(chunk => chunk)
                .ToHashSet(new CommentIdEqualityComparer())
                .ToList();

            if (downloadType is DownloadType.Video)
            {
                AdjustCommentOffsets(video, commentList);
            }

            commentList.Sort(new CommentOffsetComparer());
            return commentList;
        }

        /// <summary>
        /// Splits the download time range into work units. Using more units than workers lets a worker that
        /// draws a low-traffic stretch pick up another one, instead of one worker doing a whole busy stretch
        /// while the rest idle. The oversampling factor is bounded so sparse chats only pay for a handful of
        /// extra "start at offset" requests, and the returned ranges always tile [videoStart, videoEnd) exactly.
        /// </summary>
        internal static IReadOnlyList<Range> GenerateDownloadRanges(int videoStart, int videoEnd, int connectionCount)
        {
            const int ChunksPerWorker = 8;
            const int MinChunkSeconds = 15;

            var videoDuration = videoEnd - videoStart;

            var chunkCount = 1;
            var baseChunkSize = videoDuration;
            var remainder = 0;

            if (videoDuration > 0 && connectionCount > 1)
            {
                chunkCount = connectionCount * ChunksPerWorker;
                chunkCount = Math.Min(chunkCount, Math.Max(connectionCount, (int)Math.Ceiling(videoDuration / (double)MinChunkSeconds)));
                chunkCount = Math.Min(chunkCount, videoDuration);
                chunkCount = Math.Max(1, chunkCount);

                baseChunkSize = videoDuration / chunkCount;
                remainder = videoDuration % chunkCount;
            }

            var ranges = new List<Range>(chunkCount);
            for (var i = 0; i < chunkCount; i++)
            {
                var start = videoStart + baseChunkSize * i + Math.Min(i, remainder);
                var end = start + baseChunkSize + (i < remainder ? 1 : 0);
                ranges.Add(new Range(start, end));
            }

            return ranges;
        }

        // Some old VODs have comments offset by up to an hour. Try to fix them based on the creation times
        private void AdjustCommentOffsets(Video video, List<Comment> commentList)
        {
            if (commentList.Count == 0)
            {
                return;
            }

            var estimatedOffset = TimeSpan.FromSeconds(commentList[0].content_offset_seconds) - (commentList[0].created_at - video.created_at);
            estimatedOffset = TimeSpan.FromTicks(
                (long)Math.Min(estimatedOffset.Ticks, commentList[0].content_offset_seconds * TimeSpan.TicksPerSecond)
            );

            // If comments are within a few seconds, they're probably roughly in sync with the video
            if (estimatedOffset.TotalSeconds < 5)
            {
                return;
            }

            _progress.LogInfo($"Video comments are offset by ~{estimatedOffset.TotalSeconds:F0} seconds. Adjusting offsets...");

            var commentSpan = CollectionsMarshal.AsSpan(commentList);
            foreach (var comment in commentSpan)
            {
                comment.content_offset_seconds -= estimatedOffset.TotalSeconds;
            }
        }

        private async Task EmbedImages(ChatRoot chatRoot, CancellationToken cancellationToken, Action<int> reportProgress = null)
        {
            // When a reporter is provided the caller owns the status text and this stage reports 0-100
            // (download phase = 0-50, embedding phase = 50-100).
            void ReportFetchProgress(int percent)
            {
                if (reportProgress is null)
                {
                    _progress.ReportProgress(percent);
                }
                else
                {
                    reportProgress(percent / 2);
                }
            }

            void ReportEmbedProgress(int percent)
            {
                if (reportProgress is null)
                {
                    _progress.ReportProgress(percent);
                }
                else
                {
                    reportProgress(50 + percent / 2);
                }
            }

            if (reportProgress is null)
            {
                _progress.SetTemplateStatus("Downloading Embed Images {0}%", 0);
            }

            chatRoot.embeddedData = new EmbeddedData();

            // This is the exact same process as in ChatUpdater.cs but not in a task oriented manner
            // TODO: Combine this with ChatUpdater in a different file
            // The emote, badge, and bit caches are independent, so fetch them concurrently (as ChatUpdater does)
            var completedStages = 0;
            void ReportStageComplete() => ReportFetchProgress(Interlocked.Increment(ref completedStages) * 25);

            async Task<List<TwitchEmote>> FetchThirdPartyEmotesAsync()
            {
                var emotes = await TwitchHelper.GetThirdPartyEmotes(chatRoot.comments, chatRoot.streamer.id, _cacheDir, _progress, bttv: downloadOptions.BttvEmotes, ffz: downloadOptions.FfzEmotes, stv: downloadOptions.StvEmotes, cancellationToken: cancellationToken);
                ReportStageComplete();
                return emotes;
            }

            async Task<List<TwitchEmote>> FetchFirstPartyEmotesAsync()
            {
                var emotes = await TwitchHelper.GetEmotes(chatRoot.comments, _cacheDir, _progress, cancellationToken: cancellationToken);
                ReportStageComplete();
                return emotes;
            }

            async Task<List<ChatBadge>> FetchChatBadgesAsync()
            {
                var badges = await TwitchHelper.GetChatBadges(chatRoot.comments, chatRoot.streamer.id, _cacheDir, _progress, cancellationToken: cancellationToken);
                ReportStageComplete();
                return badges;
            }

            async Task<List<CheerEmote>> FetchBitsAsync()
            {
                var bits = await TwitchHelper.GetBits(chatRoot.comments, _cacheDir, chatRoot.streamer.id.ToString(), _progress, cancellationToken: cancellationToken);
                ReportStageComplete();
                return bits;
            }

            var thirdPartyEmoteTask = FetchThirdPartyEmotesAsync();
            var firstPartyEmoteTask = FetchFirstPartyEmotesAsync();
            var chatBadgeTask = FetchChatBadgesAsync();
            var bitTask = FetchBitsAsync();

            await Task.WhenAll(thirdPartyEmoteTask, firstPartyEmoteTask, chatBadgeTask, bitTask);

            List<TwitchEmote> thirdPartyEmotes = thirdPartyEmoteTask.Result;
            List<TwitchEmote> firstPartyEmotes = firstPartyEmoteTask.Result;
            List<ChatBadge> twitchBadges = chatBadgeTask.Result;
            List<CheerEmote> twitchBits = bitTask.Result;

            if (reportProgress is null)
            {
                _progress.SetTemplateStatus("Embedding Images {0}%", 0);
            }

            var totalImageCount = thirdPartyEmotes.Count + firstPartyEmotes.Count + twitchBadges.Count + twitchBits.Count;
            var imagesProcessed = 0;

            foreach (TwitchEmote emote in thirdPartyEmotes)
            {
                var newEmote = new EmbedEmoteData
                {
                    id = emote.Id,
                    imageScale = emote.ImageScale,
                    data = emote.ImageData,
                    name = emote.Name,
                    width = emote.Width / emote.ImageScale,
                    height = emote.Height / emote.ImageScale,
                    isZeroWidth = emote.IsZeroWidth,
                };

                chatRoot.embeddedData.thirdParty.Add(newEmote);
                ReportEmbedProgress(++imagesProcessed * 100 / totalImageCount);
            }

            cancellationToken.ThrowIfCancellationRequested();

            foreach (TwitchEmote emote in firstPartyEmotes)
            {
                var newEmote = new EmbedEmoteData
                {
                    id = emote.Id,
                    imageScale = emote.ImageScale,
                    data = emote.ImageData,
                    width = emote.Width / emote.ImageScale,
                    height = emote.Height / emote.ImageScale,
                };

                chatRoot.embeddedData.firstParty.Add(newEmote);
                ReportEmbedProgress(++imagesProcessed * 100 / totalImageCount);
            }

            cancellationToken.ThrowIfCancellationRequested();

            foreach (ChatBadge badge in twitchBadges)
            {
                var newBadge = new EmbedChatBadge
                {
                    name = badge.Name,
                    versions = badge.VersionsData
                };

                chatRoot.embeddedData.twitchBadges.Add(newBadge);
                ReportEmbedProgress(++imagesProcessed * 100 / totalImageCount);
            }

            cancellationToken.ThrowIfCancellationRequested();

            foreach (CheerEmote bit in twitchBits)
            {
                var newBit = new EmbedCheerEmote
                {
                    prefix = bit.prefix,
                    tierList = new Dictionary<int, EmbedEmoteData>()
                };

                foreach (KeyValuePair<int, TwitchEmote> emotePair in bit.tierList)
                {
                    EmbedEmoteData newEmote = new EmbedEmoteData
                    {
                        id = emotePair.Value.Id,
                        imageScale = emotePair.Value.ImageScale,
                        data = emotePair.Value.ImageData,
                        name = emotePair.Value.Name,
                        width = emotePair.Value.Width / emotePair.Value.ImageScale,
                        height = emotePair.Value.Height / emotePair.Value.ImageScale
                    };
                    newBit.tierList.Add(emotePair.Key, newEmote);
                }

                chatRoot.embeddedData.twitchBits.Add(newBit);
                ReportEmbedProgress(++imagesProcessed * 100 / totalImageCount);
            }
        }

        private async Task BackfillUserInfo(ChatRoot chatRoot, CancellationToken cancellationToken, Action<int> reportProgress = null)
        {
            void Report(int percent)
            {
                if (reportProgress is null)
                {
                    _progress.ReportProgress(percent);
                }
                else
                {
                    reportProgress(percent);
                }
            }

            // Best effort, but if we fail oh well
            if (reportProgress is null)
            {
                _progress.SetTemplateStatus("Backfilling Commenter Info {0}%", 0);
            }

            var userIds = chatRoot.comments.Select(x => x.commenter._id).Distinct().ToArray();
            if (userIds.Length == 0)
            {
                Report(100);
                return;
            }

            var userInfo = new ConcurrentDictionary<string, User>();
            var failedInfo = 0;
            var completedBatches = 0;
            const int BATCH_SIZE = 100;
            const int MAX_PARALLEL_REQUESTS = 6;

            var batchCount = (userIds.Length + BATCH_SIZE - 1) / BATCH_SIZE;

            await Parallel.ForEachAsync(Enumerable.Range(0, batchCount), new ParallelOptions
            {
                MaxDegreeOfParallelism = MAX_PARALLEL_REQUESTS,
                CancellationToken = cancellationToken
            }, async (batchIndex, _) =>
            {
                var offset = batchIndex * BATCH_SIZE;
                var userSubset = new ArraySegment<string>(userIds, offset, Math.Min(BATCH_SIZE, userIds.Length - offset));

                try
                {
                    GqlUserInfoResponse userInfoResponse;
                    using (await ChatGqlLimiter.AcquireAsync(cancellationToken).ConfigureAwait(false))
                    {
                        userInfoResponse = await TwitchHelper.GetUserInfo(userSubset);
                        ChatGqlLimiter.ReportSuccess();
                    }

                    foreach (var user in userInfoResponse.data.users)
                    {
                        userInfo[user.id] = user;
                    }
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    ChatGqlLimiter.ReportThrottled(TimeSpan.FromSeconds(5));
                    Interlocked.Increment(ref failedInfo);
                    _progress.LogVerbose($"Twitch rate limit reached while backfilling commenters {offset}-{Math.Min(offset + BATCH_SIZE, userIds.Length - 1)}.");
                }
                catch (Exception e)
                {
                    Interlocked.Increment(ref failedInfo);
                    _progress.LogVerbose($"An error occurred while backfilling commenters {offset}-{Math.Min(offset + BATCH_SIZE, userIds.Length - 1)}: {e.Message}");
                }

                Report(Interlocked.Increment(ref completedBatches) * 100 / batchCount);
            });

            Report(100);

            if (Volatile.Read(ref failedInfo) > 0)
            {
                _progress.LogInfo("Failed to backfill some commenter info");
            }

            foreach (var comment in chatRoot.comments)
            {
                if (userInfo.TryGetValue(comment.commenter._id, out var user))
                {
                    comment.commenter.updated_at = user.updatedAt;
                    comment.commenter.created_at = user.createdAt;
                    comment.commenter.bio = user.description;
                    comment.commenter.logo = user.profileImageURL;
                }
            }
        }
    }
}