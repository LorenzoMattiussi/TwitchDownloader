using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace TwitchDownloaderCore.Tools
{
    public static partial class IdParse
    {
        [GeneratedRegex("""(?<=^|twitch\.tv\/videos\/)\d+(?=\/?(?:$|\?))""")]
        private static partial Regex VideoId { get; }

        [GeneratedRegex("""(?<=^|twitch\.tv\/\w+\/v(?:ideo)?\/)\d+(?=\/?(?:$|\?))""")]
        private static partial Regex HighlightId { get; }

        [GeneratedRegex("""(?<!\d)\d{6,12}(?!\d)""")]
        private static partial Regex FileNameVideoId { get; }

        [GeneratedRegex("""(?<=^|(?:clips\.)?twitch\.tv\/(?:\w+\/clip\/)?)[\w-]+?(?=\/?(?:$|\?))""")]
        private static partial Regex ClipId { get; }

        /// <returns>A <see cref="Match"/> of the video's id or <see langword="null"/>.</returns>
        [return: MaybeNull]
        public static Match MatchVideoId(string text)
        {
            text = text.Trim();

            var videoIdMatch = VideoId.Match(text);
            if (videoIdMatch.Success)
            {
                return videoIdMatch;
            }

            var highlightIdMatch = HighlightId.Match(text);
            if (highlightIdMatch.Success)
            {
                return highlightIdMatch;
            }

            return null;
        }

        /// <returns>A <see cref="Match"/> of the clip's id or <see langword="null"/>.</returns>
        [return: MaybeNull]
        public static Match MatchClipId(string text)
        {
            text = text.Trim();

            var clipIdMatch = ClipId.Match(text);
            if (clipIdMatch.Success && !clipIdMatch.Value.All(char.IsDigit))
            {
                return clipIdMatch;
            }

            return null;
        }

        /// <returns>A <see cref="Match"/> of the video/clip's id or <see langword="null"/>.</returns>
        [return: MaybeNull]
        public static Match MatchVideoOrClipId(string text)
        {
            text = text.Trim();

            var videoIdMatch = MatchVideoId(text);
            if (videoIdMatch is { Success: true })
            {
                return videoIdMatch;
            }

            var clipIdMatch = MatchClipId(text);
            if (clipIdMatch is { Success: true })
            {
                return clipIdMatch;
            }

            return null;
        }

        /// <summary>
        /// Extracts a video id from a VOD file name such as
        /// "[2026-09-18]_Streamer_2877671523_title_vod.mp4", or from a regular id/url.
        /// </summary>
        /// <returns>A <see cref="Match"/> of the video's id or <see langword="null"/>.</returns>
        [return: MaybeNull]
        public static Match MatchVideoIdFromFileName(string text)
        {
            text = text.Trim();

            // Plain ids, video urls, and clips
            var idMatch = MatchVideoOrClipId(text);
            if (idMatch is { Success: true })
            {
                return idMatch;
            }

            // Streamer names can contain underscores (which produce empty tokens), so scan the underscore
            // separated tokens after the date instead of assuming the id is at a fixed position.
            var dateEnd = text.IndexOf(']');
            var remainder = dateEnd >= 0 ? text[(dateEnd + 1)..] : text;
            foreach (var token in remainder.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (token.Length is >= 6 and <= 12 && token.All(char.IsDigit))
                {
                    return FileNameVideoId.Match(token);
                }
            }

            // Fallback in case the id is not underscore separated (e.g. it sits right before the extension)
            var fileNameMatch = FileNameVideoId.Match(text);
            return fileNameMatch.Success ? fileNameMatch : null;
        }
    }
}