using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ScreenGuide.Skills.Windows;

namespace ScreenGuide.Skills.Windows.Tests;

public sealed class LatestMusicResolverTests
{
    [Fact]
    public async Task Resolves_release_date_and_real_title_instead_of_latest_keywords()
    {
        using var fixture = new Fixture();
        var result = await fixture.Resolver.ResolveAsync("听风");

        Assert.Equal("听风", result.Artist);
        Assert.Equal("晨光", result.ReleaseTitle);
        Assert.Equal(new DateOnly(2026, 3, 25), result.ReleaseDate);
        Assert.Equal("听风 晨光", result.SearchQuery);
        Assert.Equal("晨光", result.MatchedTitle);
        Assert.True(result.PlatformMatchFound);
        Assert.Equal(701, result.PlatformArtistId);
        Assert.DoesNotContain("最新", result.SearchQuery);
        Assert.Contains("台湾", result.CatalogScope);
        Assert.False(result.FirstReleaseVerified);
        Assert.All(fixture.Requests, uri => Assert.Equal("https", uri.Scheme));
    }

    [Fact]
    public async Task Cover_with_same_title_and_artist_name_in_title_is_not_original_artist()
    {
        using var fixture = new Fixture { PlatformArtistId = 999 };
        var result = await fixture.Resolver.ResolveAsync("听风");
        Assert.False(result.PlatformMatchFound);
        Assert.False(result.PlatformVerificationUnavailable);
    }

    [Fact]
    public async Task Platform_catalog_exact_spelling_wins_over_a_different_traditional_account()
    {
        using var fixture = new Fixture { PlatformTraditionalDuplicate = true };
        var result = await fixture.Resolver.ResolveAsync("听风");
        Assert.True(result.PlatformMatchFound);
        Assert.Equal(701, result.PlatformArtistId);
    }

    [Fact]
    public async Task Same_artist_old_album_version_does_not_prove_the_new_release_is_available()
    {
        using var fixture = new Fixture { PlatformAlbumDate = 1577836800000L };
        var result = await fixture.Resolver.ResolveAsync("听风");
        Assert.False(result.PlatformMatchFound);
    }

    [Fact]
    public async Task Future_live_and_other_artist_releases_do_not_replace_latest_original_release()
    {
        using var fixture = new Fixture();
        fixture.Albums.Insert(0, Album(503, "明日", "2027-01-01T00:00:00Z"));
        fixture.Albums.Insert(0, Album(504, "晨光 Live", "2026-10-01T00:00:00Z"));
        fixture.Albums.Insert(0, Album(505, "同名歌手", "2026-10-02T00:00:00Z", 999));
        var result = await fixture.Resolver.ResolveAsync("听风");
        Assert.Equal("晨光", result.ReleaseTitle);
    }

    [Fact]
    public async Task Old_bonus_track_is_not_selected_or_declared_a_brand_new_song()
    {
        using var fixture = new Fixture();
        fixture.Tracks.Add(Track(803, "旧时光", 13));
        var result = await fixture.Resolver.ResolveAsync("听风");
        Assert.Contains("旧时光", result.TrackTitles);
        Assert.Equal("晨光", result.MatchedTitle);
        Assert.False(result.FirstReleaseVerified);
    }

    [Fact]
    public async Task Known_old_title_is_skipped_in_favor_of_another_current_release_track()
    {
        using var fixture = new Fixture { TitleTrackHasOlderRelease = true };
        var result = await fixture.Resolver.ResolveAsync("听风");
        Assert.Equal("新的一天", result.MatchedTitle);
        Assert.Equal("听风 新的一天", result.SearchQuery);
    }

    [Fact]
    public async Task Checks_platform_availability_while_first_release_date_is_in_flight()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture { BeforeRequest = async (uri, ct) =>
        {
            if (uri.Host == "itunes.apple.com" && uri.AbsolutePath == "/search" && uri.Query.Contains("entity=song"))
                await gate.Task.WaitAsync(ct);
        } };
        var operation = fixture.Resolver.ResolveAsync("听风");
        var platformWasStarted = fixture.Requests.Any(uri => uri.Host == "music.163.com" && uri.Query.Contains("type=1&"));
        gate.SetResult();
        await operation;
        Assert.True(platformWasStarted);
    }

    [Fact]
    public async Task First_current_track_does_not_wait_for_unselected_track_lookups()
    {
        using var fixture = new Fixture();
        await fixture.Resolver.ResolveAsync("听风");
        Assert.Single(fixture.Requests, uri => uri.Host == "itunes.apple.com"
            && uri.AbsolutePath == "/search" && uri.Query.Contains("entity=song"));
    }

    [Fact]
    public async Task Exact_catalog_artist_does_not_wait_for_platform_artist_before_loading_releases()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture { BeforeRequest = async (uri, ct) =>
        {
            if (uri.Host == "music.163.com" && uri.Query.Contains("type=100")) await gate.Task.WaitAsync(ct);
        } };
        var operation = fixture.Resolver.ResolveAsync("听风");
        var albumsWereStarted = fixture.Requests.Any(uri => uri.Query.Contains("entity=album"));
        gate.SetResult();
        await operation;
        Assert.True(albumsWereStarted);
    }

    [Fact]
    public async Task Same_name_artist_ambiguity_stops_before_album_lookup()
    {
        using var fixture = new Fixture { AmbiguousArtist = true };
        var error = await Assert.ThrowsAsync<ApplicationSearchWorkflowException>(() => fixture.Resolver.ResolveAsync("听风"));
        Assert.Equal("music.artist_ambiguous", error.Code);
        Assert.DoesNotContain(fixture.Requests, uri => uri.AbsolutePath == "/lookup");
    }

    [Fact]
    public async Task Reaching_catalog_limit_is_not_treated_as_complete_catalog()
    {
        using var fixture = new Fixture();
        fixture.Albums.Clear();
        fixture.Albums.AddRange(Enumerable.Range(1, 200).Select(i => Album(500 + i, "专辑" + i, "2026-03-25T00:00:00Z")));
        var error = await Assert.ThrowsAsync<ApplicationSearchWorkflowException>(() => fixture.Resolver.ResolveAsync("听风"));
        Assert.Equal("music.catalog_incomplete", error.Code);
    }

    [Fact]
    public async Task Catalog_limit_counts_the_artist_header_too()
    {
        using var fixture = new Fixture();
        fixture.Albums.Clear();
        fixture.Albums.Add(new { wrapperType = "artist", artistId = 101, artistName = "聽風" });
        fixture.Albums.AddRange(Enumerable.Range(1, 199).Select(i => Album(500 + i, "专辑" + i, "2026-03-25T00:00:00Z")));
        var error = await Assert.ThrowsAsync<ApplicationSearchWorkflowException>(() => fixture.Resolver.ResolveAsync("听风"));
        Assert.Equal("music.catalog_incomplete", error.Code);
    }

    [Fact]
    public async Task Future_only_catalog_cannot_be_called_already_released()
    {
        using var fixture = new Fixture();
        fixture.Albums.Clear();
        fixture.Albums.Add(Album(503, "明日", "2027-01-01T00:00:00Z"));
        var error = await Assert.ThrowsAsync<ApplicationSearchWorkflowException>(() => fixture.Resolver.ResolveAsync("听风"));
        Assert.Equal("music.release_unverified", error.Code);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"resultCount\":\"wrong\",\"results\":[]}")]
    [InlineData("{\"resultCount\":2,\"results\":[]}")]
    public async Task Malformed_source_response_produces_a_readable_failure(string body)
    {
        using var handler = new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        }));
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<ApplicationSearchWorkflowException>(() => new LatestMusicResolver(client).ResolveAsync("听风"));
        Assert.Equal("music.source_response_invalid", error.Code);
    }

    [Fact]
    public async Task Oversized_response_is_rejected_before_json_parsing()
    {
        using var handler = new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('x', 2 * 1024 * 1024 + 1))
        }));
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<ApplicationSearchWorkflowException>(() => new LatestMusicResolver(client).ResolveAsync("听风"));
        Assert.Equal("music.source_too_large", error.Code);
    }

    [Fact]
    public async Task Public_platform_challenge_is_not_bypassed_or_claimed_as_no_catalog_entry()
    {
        using var fixture = new Fixture { PlatformChallenge = true };
        var result = await fixture.Resolver.ResolveAsync("听风");
        Assert.False(result.PlatformMatchFound);
        Assert.True(result.PlatformVerificationUnavailable);
        Assert.DoesNotContain(fixture.Requests, uri => uri.AbsolutePath.Contains("login"));
    }

    [Fact]
    public async Task Redirect_is_rejected_without_visiting_its_target()
    {
        using var handler = new DelegateHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://example.invalid/private");
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        var resolver = new LatestMusicResolver(client, new FrozenClock());
        var error = await Assert.ThrowsAsync<ApplicationSearchWorkflowException>(() => resolver.ResolveAsync("听风"));
        Assert.Equal("music.source_unavailable", error.Code);
    }

    [Fact]
    public async Task Cancellation_reaches_inflight_http_request()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new DelegateHandler(async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json(new { });
        });
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var operation = new LatestMusicResolver(client).ResolveAsync("听风", cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    [Fact]
    public async Task Deadline_produces_specific_error_instead_of_hanging()
    {
        using var handler = new DelegateHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json(new { });
        });
        using var client = new HttpClient(handler);
        var resolver = new LatestMusicResolver(client, totalTimeout: TimeSpan.FromMilliseconds(50));
        var error = await Assert.ThrowsAsync<ApplicationSearchWorkflowException>(() => resolver.ResolveAsync("听风"));
        Assert.Equal("music.lookup_timeout", error.Code);
    }

    private static object Album(int id, string name, string releaseDate, int artistId = 101) => new
    {
        wrapperType = "collection", collectionType = "Album", artistId, collectionId = id,
        collectionName = name, releaseDate, artistName = "聽風", trackCount = 3
    };

    private static object Track(int id, string title, int number, string date = "2026-03-25T12:00:00Z") => new
    {
        wrapperType = "track", kind = "song", artistId = 101, collectionId = 501, trackId = id,
        trackName = title, trackNumber = number, releaseDate = date
    };

    private static HttpResponseMessage Json(object data) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json")
    };

    private sealed class FrozenClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 11, 8, 0, 0, TimeSpan.Zero);
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => action(request, cancellationToken);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient _client;
        public LatestMusicResolver Resolver { get; }
        public List<Uri> Requests { get; } = [];
        public List<object> Albums { get; } = [Album(501, "晨光", "2026-03-25T07:00:00Z")];
        public List<object> Tracks { get; } = [Track(801, "晨光", 1), Track(802, "新的一天", 2)];
        public long PlatformArtistId { get; init; } = 701;
        public bool AmbiguousArtist { get; init; }
        public bool PlatformChallenge { get; init; }
        public bool TitleTrackHasOlderRelease { get; init; }
        public bool PlatformTraditionalDuplicate { get; init; }
        public long PlatformAlbumDate { get; init; } = 1774396800000L;
        public Func<Uri, CancellationToken, Task>? BeforeRequest { get; init; }

        public Fixture()
        {
            _client = new HttpClient(new DelegateHandler(async (request, ct) =>
            {
                if (BeforeRequest is { } before) await before(request.RequestUri!, ct);
                return await RespondAsync(request, ct);
            }));
            Resolver = new LatestMusicResolver(_client, new FrozenClock());
        }

        private Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            lock (Requests) Requests.Add(uri);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.Null(request.Headers.Authorization);
            var query = Uri.UnescapeDataString(uri.Query);
            if (uri.Host == "itunes.apple.com")
            {
                if (query.Contains("entity=musicArtist"))
                {
                    object[] artists = AmbiguousArtist
                        ? [new { wrapperType = "artist", artistId = 101, artistName = "聽風" }, new { wrapperType = "artist", artistId = 102, artistName = "听风" }]
                        : [new { wrapperType = "artist", artistId = 101, artistName = "聽風" }];
                    return Task.FromResult(Json(new { resultCount = artists.Length, results = artists }));
                }
                if (uri.AbsolutePath == "/lookup" && query.Contains("entity=album"))
                    return Task.FromResult(Json(new { resultCount = Albums.Count, results = Albums }));
                if (uri.AbsolutePath == "/lookup" && query.Contains("entity=song"))
                    return Task.FromResult(Json(new { resultCount = Tracks.Count, results = Tracks }));
                if (uri.AbsolutePath == "/search" && query.Contains("entity=song"))
                {
                    var candidates = Tracks.ToList();
                    if (TitleTrackHasOlderRelease) candidates.Add(Track(700, "晨光", 1, "2020-01-01T00:00:00Z"));
                    return Task.FromResult(Json(new { resultCount = candidates.Count, results = candidates }));
                }
            }
            if (uri.Host == "music.163.com")
            {
                if (PlatformChallenge) return Task.FromResult(Json(new { code = -462, message = "fixture challenge" }));
                if (query.Contains("type=100"))
                {
                    object[] artists = PlatformTraditionalDuplicate
                        ? [new { id = 701, name = "听风" }, new { id = 702, name = "聽風" }]
                        : [new { id = 701, name = "听风" }];
                    return Task.FromResult(Json(new { code = 200, result = new { artistCount = artists.Length, artists } }));
                }
                return Task.FromResult(Json(new { code = 200, result = new
                {
                    songCount = 2, songs = new[]
                    {
                        new { id = 9001, name = "晨光", artists = new[] { new { id = PlatformArtistId, name = "听风" } }, album = new { id = 901, name = "晨光", publishTime = PlatformAlbumDate } },
                        new { id = 9002, name = "新的一天", artists = new[] { new { id = PlatformArtistId, name = "听风" } }, album = new { id = 901, name = "晨光", publishTime = PlatformAlbumDate } }
                    }
                } }));
            }
            throw new InvalidOperationException("Unexpected fixed source request: " + uri);
        }

        public void Dispose() => _client.Dispose();
    }
}
