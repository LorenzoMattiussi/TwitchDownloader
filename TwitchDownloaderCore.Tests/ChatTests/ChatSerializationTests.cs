using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TwitchDownloaderCore.Chat;
using TwitchDownloaderCore.TwitchObjects;
using TwitchDownloaderCore.TwitchObjects.Gql;

namespace TwitchDownloaderCore.Tests.ChatTests
{
    public class ChatSerializationTests
    {
        // The exact options the chat serializer used before switching to source generation.
        private static readonly JsonSerializerOptions ReflectionOptions = new()
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            AllowTrailingCommas = true
        };

        private static ChatRoot CreateChatRoot()
        {
            return new ChatRoot
            {
                FileInfo = new ChatRootInfo { Version = new ChatRootVersion(1, 4, 0), CreatedAt = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc) },
                streamer = new Streamer { name = "Stréamer", login = "streamer", id = 123 },
                clipper = new Clipper { name = "Clipper", login = "clipper", id = 456 },
                video = new Video
                {
                    title = "A title with ünïcödé <>&\"' and \n newlines",
                    description = "A description",
                    id = "123456",
                    created_at = new DateTime(2023, 5, 6, 7, 8, 9, DateTimeKind.Utc),
                    start = 0,
                    end = 100,
                    length = 100,
                    viewCount = 42,
                    game = "A game",
                    chapters =
                    [
                        new VideoChapter
                        {
                            id = "c1",
                            startMilliseconds = 0,
                            lengthMilliseconds = 1000,
                            _type = "GAME_CHANGE",
                            description = "chapter",
                            subDescription = "sub",
                            thumbnailUrl = "https://example.com/thumb.jpg",
                            gameId = "1",
                            gameDisplayName = "A game",
                            gameBoxArtUrl = "https://example.com/box.jpg"
                        }
                    ]
                },
                comments =
                [
                    new Comment
                    {
                        _id = "abc",
                        created_at = new DateTime(2023, 5, 6, 7, 8, 9, DateTimeKind.Utc),
                        channel_id = "123",
                        content_type = "video",
                        content_id = "123456",
                        content_offset_seconds = 1.5,
                        commenter = new Commenter { display_name = "Bob", _id = "1", name = "bob", bio = "hi", logo = "https://example.com/a.png" },
                        message = new Message
                        {
                            body = "Hello é Kappa Cheer100",
                            bits_spent = 100,
                            fragments =
                            [
                                new Fragment { text = "Hello é " },
                                new Fragment { text = "Kappa", emoticon = new Emoticon { emoticon_id = "25" } }
                            ],
                            user_badges = [new UserBadge { _id = "subscriber", version = "12" }],
                            user_color = "#FF0000",
                            emoticons = [new Emoticon2 { _id = "25", begin = 8, end = 13 }]
                        }
                    }
                ],
                embeddedData = new EmbeddedData
                {
                    firstParty = [new EmbedEmoteData { id = "25", imageScale = 2, data = [1, 2, 3, 4], name = "Kappa", width = 28, height = 28, isZeroWidth = false }],
                    thirdParty = [],
                    twitchBadges =
                    [
                        new EmbedChatBadge
                        {
                            name = "subscriber",
                            versions = new Dictionary<string, ChatBadgeData>
                            {
                                ["12"] = new ChatBadgeData { title = "Sub", description = "d", bytes = [9, 8, 7] }
                            }
                        }
                    ],
                    twitchBits =
                    [
                        new EmbedCheerEmote
                        {
                            prefix = "Cheer",
                            tierList = new Dictionary<int, EmbedEmoteData>
                            {
                                [100] = new EmbedEmoteData { id = "1", name = "Cheer100", width = 1, height = 1, data = [5] }
                            }
                        }
                    ]
                }
            };
        }

        [Fact]
        public void SourceGeneratedOptionsProduceIdenticalOutputToReflection()
        {
            var chatRoot = CreateChatRoot();

            var sourceGenerated = JsonSerializer.Serialize(chatRoot, ChatSerialization.Options);
            var reflection = JsonSerializer.Serialize(chatRoot, ReflectionOptions);

            Assert.Equal(reflection, sourceGenerated);
        }

        [Fact]
        public void SourceGeneratedOptionsRoundTrip()
        {
            var chatRoot = CreateChatRoot();

            var json = JsonSerializer.Serialize(chatRoot, ChatSerialization.Options);
            var roundTripped = JsonSerializer.Deserialize<ChatRoot>(json, ChatSerialization.Options);

            Assert.NotNull(roundTripped);
            Assert.Single(roundTripped.comments);
            Assert.Equal(chatRoot.comments[0].message.body, roundTripped.comments[0].message.body);
            Assert.Equal(chatRoot.comments[0].message.bits_spent, roundTripped.comments[0].message.bits_spent);
            Assert.Equal(chatRoot.comments[0].commenter.display_name, roundTripped.comments[0].commenter.display_name);
            Assert.Equal(chatRoot.embeddedData.firstParty[0].data, roundTripped.embeddedData.firstParty[0].data);
            Assert.Equal(chatRoot.embeddedData.twitchBits[0].tierList[100].name, roundTripped.embeddedData.twitchBits[0].tierList[100].name);
        }

        [Fact]
        public void WebOptionsDeserializeCamelCaseGqlComments()
        {
            const string json = """
            {
              "data": {
                "video": {
                  "id": "123456",
                  "creator": { "id": "1", "channel": { "id": "1" } },
                  "comments": {
                    "edges": [
                      {
                        "cursor": "abc",
                        "node": {
                          "id": "c1",
                          "commenter": { "id": "1", "login": "bob", "displayName": "Bob" },
                          "contentOffsetSeconds": 12,
                          "createdAt": "2023-05-06T07:08:09Z",
                          "message": {
                            "fragments": [{ "text": "hello" }],
                            "userBadges": [{ "id": "1", "setID": "subscriber", "version": "12" }],
                            "userColor": "#FF0000"
                          }
                        }
                      }
                    ],
                    "pageInfo": { "hasNextPage": false, "hasPreviousPage": false }
                  }
                }
              }
            }
            """;

            var response = JsonSerializer.Deserialize<GqlCommentResponse>(json, ChatSerialization.WebOptions);

            Assert.NotNull(response);
            Assert.Equal("123456", response.data.video.id);
            Assert.Equal("Bob", response.data.video.comments.edges[0].node.commenter.displayName);
            Assert.Equal(12, response.data.video.comments.edges[0].node.contentOffsetSeconds);
            Assert.Equal("subscriber", response.data.video.comments.edges[0].node.message.userBadges[0].setID);
        }
    }
}
