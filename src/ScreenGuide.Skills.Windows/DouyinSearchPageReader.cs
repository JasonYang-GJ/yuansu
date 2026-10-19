using Accessibility;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ScreenGuide.Skills.Windows;

internal sealed record DouyinSearchPageObservation(bool Readable, bool PageExists, string Query,
    string PageIdentity, bool Busy, IReadOnlyList<SearchResultItem> Videos);

/// <summary>Reads the observed desktop search route and native video-result list; never invokes result cards.</summary>
internal static class DouyinSearchPageReader
{
    internal static bool Applies(ForegroundWindowSnapshot window) => window.ProcessName.Equals("douyin", StringComparison.OrdinalIgnoreCase);

    internal static DouyinSearchPageObservation Read(long window, CancellationToken cancellationToken)
    {
        try
        {
            var roots = LegacyBrowserSearchSemantics.Open(window);
            if (roots.Count == 0) return Unreadable();
            var pages = new List<(LegacyBrowserSearchSemantics.Node Node, string Query)>();
            foreach (var root in roots)
            {
                var queue = new Queue<(IAccessible Accessible, int Depth, string Query)>();
                queue.Enqueue((root.Accessible, 0, ""));
                var visited = 0;
                while (queue.Count > 0 && visited++ < 1400)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (accessible, depth, query) = queue.Dequeue();
                    var node = LegacyBrowserSearchSemantics.ReadNode(accessible, window);
                    if (node is null) continue;
                    if (node.Role == 15) query = QueryFromPublicDocumentUri(accessible.get_accValue(0)) ?? "";
                    if (node.Attributes.GetValueOrDefault("id") == "search-content-area")
                    {
                        pages.Add((node, query));
                        continue;
                    }
                    if (depth >= 14) continue;
                    // Search results are under the application's content area, not its chat/account portals.
                    var id = node.Attributes.GetValueOrDefault("id") ?? "";
                    if (id.StartsWith("im-", StringComparison.Ordinal) || id is "douyin-sidebar" or "douyin-header") continue;
                    foreach (var child in LegacyBrowserSearchSemantics.Children(accessible)) queue.Enqueue((child, depth + 1, query));
                }
                if (queue.Count > 0) return Unreadable();
            }
            if (pages.Count == 0) return new(true, false, "", "", false, []);
            if (pages.Count != 1 || pages[0].Query.Length == 0) return Unreadable();
            var (page, pageQuery) = pages[0];
            if ((page.State & (0x8000 | 0x10000)) != 0) return Unreadable();
            var result = Nodes(page.Accessible, window).SingleOrDefault(node => node.Attributes.GetValueOrDefault("id") == "search-result-container");
            if (result is null) return Unreadable();
            var list = Nodes(result.Accessible, window).SingleOrDefault(node => node.Role == 33 && node.Attributes.GetValueOrDefault("tag") == "ul");
            if (list is null) return Unreadable();
            var rows = Nodes(list.Accessible, window);
            if (rows.Length == 0 || rows.Any(node => node.Role != 34 || node.Attributes.GetValueOrDefault("tag") != "li")) return Unreadable();
            if ((rows[0].State & (0x8000 | 0x10000)) != 0) return Unreadable();
            var busy = IsBusy(page) || IsBusy(result) || IsBusy(list);
            var videos = new List<SearchResultItem>();
            foreach (var row in rows.Take(6))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var identities = new List<string>();
                var queue = new Queue<(LegacyBrowserSearchSemantics.Node Node, int Depth)>();
                queue.Enqueue((row, 0));
                var count = 0;
                while (queue.Count > 0 && count++ < 100)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (node, depth) = queue.Dequeue();
                    busy |= IsBusy(node);
                    if (node.Attributes.GetValueOrDefault("id") == "sliderVideo")
                    {
                        if (!IsReadyMediaNode(node.State, node.Attributes.GetValueOrDefault("busy"),
                            node.Attributes.GetValueOrDefault("class"), requireVisible: videos.Count == 0)) return Unreadable();
                        var mediaId = VideoIdentity(node.Attributes.GetValueOrDefault("class"));
                        if (mediaId.Length > 0) identities.Add(mediaId);
                        continue; // Do not inspect video comments, author profiles or player internals.
                    }
                    if (depth >= 4) continue;
                    foreach (var child in Nodes(node.Accessible, window)) queue.Enqueue((child, depth + 1));
                }
                if (queue.Count > 0 || identities.Count > 1) return Unreadable();
                if (identities.Count == 0) break; // Virtualized off-screen rows may not have loaded yet.
                var identity = identities[0];
                videos.Add(new(identity, "视频 " + identity["douyin-video:".Length..], new ReadOnlyVideoReceipt(identity)));
            }
            return new(true, true, pageQuery, page.Identity, busy, videos);
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or ArgumentException or InvalidCastException)
        { return Unreadable(); }
    }

    internal static string? QueryFromPublicDocumentUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort || uri.UserInfo.Length > 0 || !uri.Host.Equals("www.douyin.com", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = uri.AbsolutePath.Split('/');
        if (parts.Length != 4 || parts[1] != "jingxuan" || parts[2] != "search") return null;
        var query = Uri.UnescapeDataString(parts[3]);
        return query.Length is > 0 and <= 200 && !query.Any(char.IsControl) ? query : null;
    }

    internal static string VideoIdentity(string? classes)
    {
        var tokens = (classes ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var videos = tokens.Where(token => Regex.IsMatch(token, "^video_[0-9]{1,24}$", RegexOptions.CultureInvariant)).ToArray();
        return videos.Length == 1 ? "douyin-video:" + videos[0][6..] : "";
    }

    internal static bool IsVerifiedResultForQuery(DouyinSearchPageObservation before, DouyinSearchPageObservation after, string query) =>
        before.Readable && after.Readable && after.PageExists && after.Query == query && !after.Busy && after.Videos.Count > 0
        && (!before.PageExists || before.Query == query
            || before.Videos.Count == 0 || before.Videos[0].Identity != after.Videos[0].Identity);

    private static LegacyBrowserSearchSemantics.Node[] Nodes(IAccessible parent, long window)
    {
        if (!LegacyBrowserSearchSemantics.TryReadCompleteSearchChildren(parent, out var children)) throw new InvalidOperationException();
        return children.Select(child => LegacyBrowserSearchSemantics.ReadNode(child, window) ?? throw new InvalidOperationException()).ToArray();
    }

    internal static bool IsReadyMediaNode(int state, string? busy, string? classes, bool requireVisible) =>
        !IsBusyMetadata(state, busy, classes) && (!requireVisible || (state & (0x8000 | 0x10000)) == 0);

    private static bool IsBusy(LegacyBrowserSearchSemantics.Node node) =>
        IsBusyMetadata(node.State, node.Attributes.GetValueOrDefault("busy"), node.Attributes.GetValueOrDefault("class"));
    private static bool IsBusyMetadata(int state, string? busy, string? classes) => (state & 0x800) != 0
        || busy == "true"
        || (classes ?? "").Split(' ').Any(token => token.Contains("skeleton", StringComparison.OrdinalIgnoreCase));
    private static DouyinSearchPageObservation Unreadable() => new(false, false, "", "", false, []);
    private sealed record ReadOnlyVideoReceipt(string Identity);
}
