using System.Net.Http;
using System.Net;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

namespace ScreenGuide.Skills.Windows;

public interface ILatestMusicResolver
{
    Task<LatestMusicResolution> ResolveAsync(string artist, CancellationToken cancellationToken = default);
}

public sealed record LatestMusicResolution(
    string Artist,
    string ReleaseTitle,
    DateOnly ReleaseDate,
    IReadOnlyList<string> TrackTitles,
    string SearchQuery,
    bool PlatformMatchFound,
    IReadOnlyList<string> Sources)
{
    public string MatchedTitle { get; init; } = string.Empty;
    public long? PlatformArtistId { get; init; }
    public DateTimeOffset CheckedAt { get; init; }
    public string CatalogScope { get; init; } = "Apple 台湾地区公开发行目录";
    public bool FirstReleaseVerified { get; init; }
    public bool PlatformVerificationUnavailable { get; init; }
}

public sealed class LatestMusicResolver : ILatestMusicResolver
{
    private const int CatalogLimit = 200;
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private static readonly HttpClient PublicClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        ConnectTimeout = TimeSpan.FromSeconds(3),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _totalTimeout;

    public LatestMusicResolver() : this(PublicClient) { }

    public LatestMusicResolver(HttpClient httpClient, TimeProvider? timeProvider = null, TimeSpan? totalTimeout = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _clock = timeProvider ?? TimeProvider.System;
        _totalTimeout = totalTimeout ?? TimeSpan.FromSeconds(12);
        if (_totalTimeout <= TimeSpan.Zero || _totalTimeout > TimeSpan.FromSeconds(60))
            throw new ArgumentOutOfRangeException(nameof(totalTimeout));
    }

    public async Task<LatestMusicResolution> ResolveAsync(string artist, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(artist) || artist.Length > 80 || artist.Any(char.IsControl))
            throw Failure("artist_invalid", "请说清楚需要查找新歌的歌手名字。");
        var requestedArtist = Display(artist);
        using var deadline = new CancellationTokenSource(_totalTimeout, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            return await ResolveCoreAsync(requestedArtist, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Failure("lookup_timeout", "发行信息查询超时，尚未确认最新歌曲，请稍后重试。");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw Failure("source_unavailable", "暂时无法连接公开音乐目录，尚未确认最新发行信息。");
        }
        catch (JsonException)
        {
            throw Failure("source_response_invalid", "公开音乐目录返回的信息不完整，暂时不能确认最新歌曲。");
        }
        finally
        {
            await linked.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task<LatestMusicResolution> ResolveCoreAsync(string requestedArtist, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().ToOffset(TimeSpan.FromHours(8)).DateTime);
        var artistTask = ReadAppleAsync("search?term=" + Escape(requestedArtist) + "&entity=musicArtist&country=TW&limit=200", cancellationToken);
        var platformTask = ResolvePlatformArtistAsync(requestedArtist, cancellationToken);
        using var artistDocument = await artistTask.ConfigureAwait(false);
        var artistRows = AppleResults(artistDocument);
        if (artistRows.Length >= CatalogLimit)
            throw Failure("catalog_incomplete", "歌手查询结果超过可核实范围，请使用更完整的歌手名字。");
        var artists = artistRows.Where(row => Text(row, "wrapperType") == "artist")
            // The platform artist lookup accepts this same normalized spelling only;
            // it cannot add authority to the catalog match or needlessly block releases.
            .Where(row => Same(Text(row, "artistName"), requestedArtist))
            .Where(row => Number(row, "artistId") > 0)
            .GroupBy(row => Number(row, "artistId")).Select(group => group.First()).ToArray();
        if (artists.Length == 0)
            throw Failure("artist_not_found", "暂时没有在官方发行目录中确认这位歌手，请使用歌手的完整名字。");
        if (artists.Length != 1)
            throw Failure("artist_ambiguous", "发行目录中有同名歌手，暂时不能确定你要找哪一位。");
        var artistId = Number(artists[0], "artistId");
        var artist = Display(Text(artists[0], "artistName"));

        using var albumDocument = await ReadAppleAsync($"lookup?id={artistId}&entity=album&country=TW&limit=200&sort=recent", cancellationToken).ConfigureAwait(false);
        var albumResults = AppleResults(albumDocument);
        if (albumResults.Length >= CatalogLimit)
            throw Failure("catalog_incomplete", "这位歌手的发行目录超出本次可完整核实的范围，不能确认最新一张。");
        var albumRows = albumResults.Where(row => Text(row, "wrapperType") == "collection").ToArray();
        var releases = new List<Release>();
        foreach (var row in albumRows.Where(row => Number(row, "artistId") == artistId))
        {
            var title = Text(row, "collectionName");
            var date = ReleaseDate(row, "releaseDate");
            if (!ValidTitle(title) || date is null || Number(row, "collectionId") <= 0)
                throw Failure("catalog_incomplete", "发行目录缺少名称或日期，暂时不能确认最新发行。");
            if (date <= today && !Repackaged(title))
                releases.Add(new Release(Number(row, "collectionId"), Display(title), date.Value));
        }
        var release = releases.OrderByDescending(item => item.Date).ThenBy(item => item.Title, StringComparer.Ordinal).FirstOrDefault()
            ?? throw Failure("release_unverified", "暂时没有核实到这位歌手已经发行的原版专辑或单曲；预告、现场版和重制版不会当作新歌。");

        using var tracksDocument = await ReadAppleAsync($"lookup?id={release.Id}&entity=song&country=TW&limit=200", cancellationToken).ConfigureAwait(false);
        var trackResults = AppleResults(tracksDocument);
        if (trackResults.Length >= CatalogLimit)
            throw Failure("catalog_incomplete", "最新发行作品的曲目太多，未能完整核实本次曲目。");
        var trackRows = trackResults.Where(row => Text(row, "kind") == "song").ToArray();
        var tracks = trackRows.Where(row => Number(row, "artistId") == artistId && Number(row, "collectionId") == release.Id)
            .Where(row => Number(row, "trackId") > 0 && ValidTitle(Text(row, "trackName")))
            .Select(row => new Track(Number(row, "trackId"), Display(Text(row, "trackName")), ReleaseDate(row, "releaseDate"), Number(row, "trackNumber")))
            .Where(track => track.Date is not null && track.Date <= today)
            .OrderBy(track => track.Number).ThenBy(track => track.Id).ToArray();
        if (tracks.Length == 0)
            throw Failure("track_unverified", "已经查到最近发行的作品，但尚未核实到属于这位歌手的具体曲目。");

        // A new album date is not proof that each song is a new composition. Prefer the
        // title track and exclude known older recordings. Check the candidate's platform
        // availability concurrently; unselected tracks never delay a valid first choice.
        var candidates = tracks.Where(track => !Repackaged(track.Title) && track.Date >= release.Date)
            .OrderByDescending(track => Same(track.Title, WithoutReleaseSuffix(release.Title)))
            .ThenBy(track => track.Number).Take(3).ToArray();
        Track? selected = null;
        PlatformAvailability? availability = null;
        foreach (var candidate in candidates)
        {
            var earlierDateTask = EarliestKnownDateAsync(artistId, artist, candidate, cancellationToken);
            var availabilityTask = VerifyCandidateAsync(candidate);
            await Task.WhenAll(earlierDateTask, availabilityTask).ConfigureAwait(false);
            var earlierDate = await earlierDateTask.ConfigureAwait(false);
            if (earlierDate is not null && earlierDate < release.Date) continue;
            selected = candidate;
            availability = await availabilityTask.ConfigureAwait(false);
            break;
        }
        if (selected is null || availability is null)
            throw Failure("track_unverified", "最近发行作品中的候选曲目已有更早版本，暂时不能把这些重收录歌曲当成新歌。");
        var query = artist + " " + selected.Title;
        if (query.Length > 200 || query.Any(char.IsControl))
            throw Failure("track_unverified", "歌曲名称过长，无法可靠地提交搜索。");
        var platform = await platformTask.ConfigureAwait(false);
        var sources = new List<string>
        {
            $"https://music.apple.com/tw/artist/{artistId}",
            $"https://music.apple.com/tw/album/{release.Id}"
        };
        if (platform.Id is not null) sources.Add($"https://music.163.com/artist?id={platform.Id.Value}");
        return new LatestMusicResolution(artist, release.Title, release.Date,
            tracks.Select(track => track.Title).Distinct(StringComparer.Ordinal).ToArray(), query, availability.Found, sources)
        {
            MatchedTitle = selected.Title,
            PlatformArtistId = platform.Id,
            CheckedAt = _clock.GetUtcNow(),
            PlatformVerificationUnavailable = availability.Unavailable,
            // The public catalog describes releases/versions, not worldwide first release.
            FirstReleaseVerified = false
        };

        async Task<PlatformAvailability> VerifyCandidateAsync(Track candidate)
        {
            var performer = await platformTask.ConfigureAwait(false);
            return performer.Id is null ? new PlatformAvailability(false, performer.Unavailable)
                : await VerifyPlatformTrackAsync(performer.Id.Value, artist, candidate.Title, release, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<DateOnly?> EarliestKnownDateAsync(long artistId, string artist, Track track, CancellationToken cancellationToken)
    {
        using var document = await ReadAppleAsync("search?term=" + Escape(artist + " " + track.Title)
            + "&entity=song&country=TW&limit=200", cancellationToken).ConfigureAwait(false);
        return AppleResults(document).Where(row => Text(row, "kind") == "song" && Number(row, "artistId") == artistId)
            .Where(row => Same(Text(row, "trackName"), track.Title))
            .Select(row => ReleaseDate(row, "releaseDate")).Where(date => date is not null).Min();
    }

    private async Task<PlatformArtist> ResolvePlatformArtistAsync(string artist, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await ReadNetEaseAsync("s=" + Escape(artist) + "&type=100&offset=0&limit=100", cancellationToken).ConfigureAwait(false);
            var rows = NestedRows(document.RootElement, "result", "artists");
            var exact = rows.Where(row => Number(row, "id") > 0 && SameSpelling(Text(row, "name"), artist)).ToArray();
            // Keep an exact catalog name ahead of a different account whose spelling only
            // collapses to it after Chinese conversion. Multiple exact names remain ambiguous.
            if (exact.Length == 0)
                exact = rows.Where(row => Number(row, "id") > 0 && Same(Text(row, "name"), artist)).ToArray();
            // Alias-only matches are not enough to choose between unrelated performers.
            if (exact.Length != 1) return new PlatformArtist(null, null, true);
            return new PlatformArtist(Number(exact[0], "id"), Display(Text(exact[0], "name")), false);
        }
        catch (Exception exception) when (exception is ApplicationSearchWorkflowException or HttpRequestException or IOException or JsonException)
        {
            return new PlatformArtist(null, null, true);
        }
    }

    private async Task<PlatformAvailability> VerifyPlatformTrackAsync(long artistId, string artist, string title, Release release, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await ReadNetEaseAsync("s=" + Escape(artist + " " + title) + "&type=1&offset=0&limit=100", cancellationToken).ConfigureAwait(false);
            var found = NestedRows(document.RootElement, "result", "songs").Any(song =>
                Same(Text(song, "name"), title)
                && Rows(song, "artists").Any(singer => Number(singer, "id") == artistId)
                && song.TryGetProperty("album", out var album)
                && Same(WithoutReleaseSuffix(Text(album, "name")), WithoutReleaseSuffix(release.Title))
                && UnixReleaseDate(album, "publishTime") == release.Date);
            return new PlatformAvailability(found, false);
        }
        catch (Exception exception) when (exception is ApplicationSearchWorkflowException or HttpRequestException or IOException or JsonException)
        {
            return new PlatformAvailability(false, true);
        }
    }

    private Task<JsonDocument> ReadAppleAsync(string path, CancellationToken cancellationToken)
        => ReadJsonAsync(new Uri("https://itunes.apple.com/" + path), cancellationToken);

    private async Task<JsonDocument> ReadNetEaseAsync(string query, CancellationToken cancellationToken)
    {
        var document = await ReadJsonAsync(new Uri("https://music.163.com/api/search/get/web?" + query), cancellationToken).ConfigureAwait(false);
        if (Number(document.RootElement, "code") == 200) return document;
        document.Dispose();
        throw Failure("source_unavailable", "网易云公开目录暂时无法核实这首歌曲。");
    }

    private async Task<JsonDocument> ReadJsonAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("Yuanshu/0.7");
        if (uri.Host == "music.163.com") request.Headers.Referrer = new Uri("https://music.163.com/");
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.RequestMessage?.RequestUri is { } receivedUri && receivedUri != uri)
            throw Failure("source_unavailable", "公开音乐目录暂时不可用，尚未确认最新发行信息。");
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            throw Failure("source_too_large", "公开音乐目录返回的数据过大，已停止本次核实。");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaximumResponseBytes)
                throw Failure("source_too_large", "公开音乐目录返回的数据过大，已停止本次核实。");
            buffer.Write(chunk, 0, read);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
        document.Dispose();
        throw Failure("source_response_invalid", "公开音乐目录返回的信息格式不正确，已停止本次核实。");
    }

    private static JsonElement[] AppleResults(JsonDocument document)
    {
        var rows = Rows(document.RootElement, "results");
        if (!document.RootElement.TryGetProperty("resultCount", out var count) || count.ValueKind != JsonValueKind.Number
            || !count.TryGetInt32(out var total) || total != rows.Length)
            throw Failure("source_response_invalid", "公开音乐目录返回的信息不完整，暂时不能确认最新发行。");
        return rows;
    }

    private static JsonElement[] NestedRows(JsonElement row, string container, string name)
        => row.TryGetProperty(container, out var parent) ? Rows(parent, name) : [];
    private static JsonElement[] Rows(JsonElement row, string name)
        => row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().ToArray() : [];
    private static string Text(JsonElement row, string name)
        => row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : string.Empty;
    private static long Number(JsonElement row, string name)
        => row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;
    private static DateOnly? ReleaseDate(JsonElement row, string name)
        => DateTimeOffset.TryParse(Text(row, name), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? DateOnly.FromDateTime(date.ToOffset(TimeSpan.FromHours(8)).DateTime) : null;
    private static DateOnly? UnixReleaseDate(JsonElement row, string name)
    {
        var value = Number(row, name);
        if (value <= 0 || value > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()) return null;
        return DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(value).ToOffset(TimeSpan.FromHours(8)).DateTime);
    }
    private static bool ValidTitle(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 140 && !value.Any(char.IsControl);
    private static string Display(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC);
        if (normalized.Length == 0) return normalized;
        const uint simplifiedChinese = 0x02000000;
        var length = LCMapStringEx("zh-CN", simplifiedChinese, normalized, normalized.Length, null, 0, IntPtr.Zero, IntPtr.Zero, 0);
        if (length == 0) throw Failure("source_response_invalid", "暂时无法可靠比对歌手名字的简繁体写法。");
        var converted = new char[length];
        if (LCMapStringEx("zh-CN", simplifiedChinese, normalized, normalized.Length, converted, converted.Length, IntPtr.Zero, IntPtr.Zero, 0) == 0)
            throw Failure("source_response_invalid", "暂时无法可靠比对歌手名字的简繁体写法。");
        return Regex.Replace(new string(converted), @"\s+", " ").Trim();
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int LCMapStringEx(string localeName, uint mapFlags, string source, int sourceLength,
        [Out] char[]? destination, int destinationLength, IntPtr version, IntPtr reserved, nint sortHandle);
    private static bool Same(string left, string right) => string.Equals(Display(left), Display(right), StringComparison.OrdinalIgnoreCase);
    private static bool SameSpelling(string left, string right) => string.Equals(left.Normalize(NormalizationForm.FormKC).Trim(), right.Normalize(NormalizationForm.FormKC).Trim(), StringComparison.OrdinalIgnoreCase);
    private static string WithoutReleaseSuffix(string title) => Regex.Replace(title, @"\s*[-–—]\s*(?:Single|EP)\s*$", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
    private static bool Repackaged(string title) => Regex.IsMatch(Display(title),
        @"(?:现场|演唱会|演唱會|精选|精選|重制|重製|重录|重錄|翻唱|伴奏|混音|周年|纪念|紀念|豪华|豪華|典藏|合辑|合集|\b(?:live|remix|remaster(?:ed)?|deluxe|compilation|anniversary|greatest hits|best of|instrumental|karaoke|cover)\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static string Escape(string value) => Uri.EscapeDataString(value);
    private static ApplicationSearchWorkflowException Failure(string code, string message) => new("music." + code, message);
    private sealed record Release(long Id, string Title, DateOnly Date);
    private sealed record Track(long Id, string Title, DateOnly? Date, long Number);
    private sealed record PlatformArtist(long? Id, string? Name, bool Unavailable);
    private sealed record PlatformAvailability(bool Found, bool Unavailable);
}
