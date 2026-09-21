using TwitchDownloaderCore.Tools;

namespace TwitchDownloaderCore.Tests.ToolTests
{
    // ReSharper disable StringLiteralTypo
    public class IdParseTests
    {
        [Theory]
        [InlineData("41546181")] // Oldest VODs - 8
        [InlineData("982306410")] // Old VODs - 9
        [InlineData("6834869128")] // Current VODs - 10
        [InlineData("11987163407")] // Future VODs - 11
        public void CorrectlyParsesVodId(string id)
        {
            var match = IdParse.MatchVideoId(id);

            Assert.NotNull(match);
            Assert.Equal(id, match.Value);
        }

        [Theory]
        [InlineData("https://www.twitch.tv/videos/41546181", "41546181")] // Oldest VODs - 8
        [InlineData("https://www.twitch.tv/videos/982306410", "982306410")] // Old VODs - 9
        [InlineData("https://www.twitch.tv/videos/6834869128", "6834869128")] // Current VODs - 10
        [InlineData("https://www.twitch.tv/videos/11987163407", "11987163407")] // Future VODs - 11
        [InlineData("https://www.twitch.tv/kitboga/video/2865132173", "2865132173")] // Alternate highlight URL
        [InlineData("https://www.twitch.tv/kitboga/v/2865132173", "2865132173")] // Alternate highlight URL
        [InlineData("https://www.twitch.tv/videos/4894164023/", "4894164023")]
        public void CorrectlyParsesVodLink(string link, string expectedId)
        {
            var match = IdParse.MatchVideoId(link);

            Assert.NotNull(match);
            Assert.Equal(expectedId, match.Value);
        }

        [Theory]
        [InlineData("SpineyPieTwitchRPGNurturing")]
        [InlineData("FuriousFlaccidTireArgieB8-NHbTiYQlzwHVvv_Vf")]
        public void CorrectlyParsesClipId(string id)
        {
            var match = IdParse.MatchClipId(id);

            Assert.NotNull(match);
            Assert.Equal(id, match.Value);
        }

        [Theory]
        [InlineData("https://www.twitch.tv/streamer8/clip/SpineyPieTwitchRPGNurturing", "SpineyPieTwitchRPGNurturing")]
        [InlineData("https://www.twitch.tv/streamer8/clip/FuriousFlaccidTireArgieB8-NHbTiYQlzwHVvv_Vf", "FuriousFlaccidTireArgieB8-NHbTiYQlzwHVvv_Vf")]
        [InlineData("https://www.twitch.tv/streamer8/clip/SpineyPieTwitchRPGNurturing?featured=false&filter=clips&range=all&sort=time", "SpineyPieTwitchRPGNurturing")]
        [InlineData("https://www.twitch.tv/streamer8/clip/FuriousFlaccidTireArgieB8-NHbTiYQlzwHVvv_Vf?featured=false&filter=clips&range=all&sort=time", "FuriousFlaccidTireArgieB8-NHbTiYQlzwHVvv_Vf")]
        [InlineData("https://clips.twitch.tv/SpineyPieTwitchRPGNurturing", "SpineyPieTwitchRPGNurturing")]
        [InlineData("https://clips.twitch.tv/FuriousFlaccidTireArgieB8-NHbTiYQlzwHVvv_Vf", "FuriousFlaccidTireArgieB8-NHbTiYQlzwHVvv_Vf")]
        [InlineData("https://clips.twitch.tv/SpineyPieTwitchRPGNurturing/", "SpineyPieTwitchRPGNurturing")]
        [InlineData("https://clips.twitch.tv/FuriousFlaccidTireArgieB8-NHbTiYQlzwHVvv_Vf/", "FuriousFlaccidTireArgieB8-NHbTiYQlzwHVvv_Vf")]
        public void CorrectlyParsesClipLink(string link, string expectedId)
        {
            var match = IdParse.MatchClipId(link);

            Assert.NotNull(match);
            Assert.Equal(expectedId, match.Value);
        }

        [Theory]
        [InlineData("41546181")] // Oldest VODs - 8
        [InlineData("982306410")] // Old VODs - 9
        [InlineData("6834869128")] // Current VODs - 10
        [InlineData("11987163407")] // Future VODs - 11
        [InlineData("SpineyPieTwitchRPGNurturing")]
        [InlineData("FuriousFlaccidTireArgieB8-NHbTiYQlzwHVvv_Vf")]
        public void CorrectlyParsesVodOrClipId(string id)
        {
            var match = IdParse.MatchVideoOrClipId(id);

            Assert.NotNull(match);
            Assert.Equal(id, match.Value);
        }

        [Theory]
        [InlineData("https://www.twitch.tv/videos/41546181", "41546181")] // Oldest VODs - 8
        [InlineData("https://www.twitch.tv/videos/982306410", "982306410")] // Old VODs - 9
        [InlineData("https://www.twitch.tv/videos/6834869128", "6834869128")] // Current VODs - 10
        [InlineData("https://www.twitch.tv/videos/11987163407", "11987163407")] // Future VODs - 11
        [InlineData("https://www.twitch.tv/kitboga/video/2865132173", "2865132173")] // Alternate highlight URL
        [InlineData("https://www.twitch.tv/kitboga/v/2865132173", "2865132173")] // Alternate VOD URL
        [InlineData("https://www.twitch.tv/videos/4894164023/", "4894164023")]
        [InlineData("https://www.twitch.tv/streamer8/clip/SpineyPieTwitchRPGNurturing", "SpineyPieTwitchRPGNurturing")]
        [InlineData("https://www.twitch.tv/streamer8/clip/FuriousFlaccidTireArgieB8-NHbTiYQlzwHVvv_Vf", "FuriousFlaccidTireArgieB8-NHbTiYQlzwHVvv_Vf")]
        [InlineData("https://www.twitch.tv/streamer8/clip/SpineyPieTwitchRPGNurturing?featured=false&filter=clips&range=all&sort=time", "SpineyPieTwitchRPGNurturing")]
        [InlineData("https://www.twitch.tv/streamer8/clip/FuriousFlaccidTireArgieB8-NHbTiYQlzwHVvv_Vf?featured=false&filter=clips&range=all&sort=time", "FuriousFlaccidTireArgieB8-NHbTiYQlzwHVvv_Vf")]
        [InlineData("https://clips.twitch.tv/SpineyPieTwitchRPGNurturing", "SpineyPieTwitchRPGNurturing")]
        [InlineData("https://clips.twitch.tv/FuriousFlaccidTireArgieB8-NHbTiYQlzwHVvv_Vf", "FuriousFlaccidTireArgieB8-NHbTiYQlzwHVvv_Vf")]
        [InlineData("https://clips.twitch.tv/SpineyPieTwitchRPGNurturing/", "SpineyPieTwitchRPGNurturing")]
        [InlineData("https://clips.twitch.tv/FuriousFlaccidTireArgieB8-NHbTiYQlzwHVvv_Vf/", "FuriousFlaccidTireArgieB8-NHbTiYQlzwHVvv_Vf")]
        public void CorrectlyParsesVodOrClipLink(string link, string expectedId)
        {
            var match = IdParse.MatchVideoOrClipId(link);

            Assert.NotNull(match);
            Assert.Equal(expectedId, match.Value);
        }

        [Theory]
        [InlineData("[2026-09-18]_Tumblurr_2877671523_REACTIONS FIFA 27 PACK RACE THE GROUNDS_vod.mp4", "2877671523")]
        [InlineData("[2026-09-18]_zackrawrr_2877570828_[DROPS ON] UNBANNED WOW FOREVER BETA TODAY BIG DAY HUGE DRAMA  TODAY NEW GAMES BIG NEWS  REACTS @asmongold247_vod.mp4", "2877570828")]
        [InlineData("[2026-09-19]_ControCalcio___2878378083_ROMA INTER CALDO! CASO ARBITRI INTER DE ROSSI ATTACCA FAINA E SCISCIONE!_vod.mp4", "2878378083")]
        [InlineData("[2026-09-19]_ControCalcio___2878725254__vod.mp4", "2878725254")]
        [InlineData("[2026-09-19]_DanieleBrogna_2878458888_ROMA - INTER - DIRETTA PARTITA - LIVE REACTION DI UN MILANISTA_vod.mp4", "2878458888")]
        [InlineData("[2026-09-19]_TheRealMarzaa_2878629863_TORNEO P.M. CON GIANKO_vod.mp4", "2878629863")]
        [InlineData("2877671523", "2877671523")]
        [InlineData("https://www.twitch.tv/videos/2877671523", "2877671523")]
        public void CorrectlyParsesFileNameVodId(string fileName, string expectedId)
        {
            var match = IdParse.MatchVideoIdFromFileName(fileName);

            Assert.NotNull(match);
            Assert.Equal(expectedId, match.Value);
        }

        [Fact]
        public void DoesNotParseFileNameWithoutVodId()
        {
            var match = IdParse.MatchVideoIdFromFileName("just some random file name.mp4");

            Assert.Null(match);
        }

        [Fact]
        public void DoesNotParseGarbageVodId()
        {
            const string GARBAGE = "SORRY FOR THE TRAFFIC NaM";

            var match = IdParse.MatchVideoId(GARBAGE);

            Assert.Null(match);
        }

        [Fact]
        public void DoesNotParseGarbageClipId()
        {
            const string GARBAGE = "SORRY FOR THE TRAFFIC NaM";

            var match = IdParse.MatchClipId(GARBAGE);

            Assert.Null(match);
        }

        [Fact]
        public void DoesNotParseGarbageVodOrClipId()
        {
            const string GARBAGE = "SORRY FOR THE TRAFFIC NaM";

            var match = IdParse.MatchVideoOrClipId(GARBAGE);

            Assert.Null(match);
        }
    }
}