using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Accessibility;

namespace ScreenGuide.Skills.Windows;

internal sealed record NativeSearchPageObservation(
    bool Readable, bool PageExists, string Query, string PageIdentity, string Generation,
    bool Busy, IReadOnlyList<SearchResultItem> Songs);

/// <summary>
/// NetEase desktop's observed CEF search-result structure. Reads search-page metadata only;
/// it never treats generic page text or a song title as an actionable media identity.
/// </summary>
internal static class NetEaseSearchPageReader
{
    internal static bool Applies(ForegroundWindowSnapshot window) =>
        window.ProcessName.Equals("cloudmusic", StringComparison.OrdinalIgnoreCase);

    internal static NativeSearchPageObservation Read(long window, CancellationToken cancellationToken)
    {
        try
        {
            var roots = LegacyBrowserSearchSemantics.Open(window);
            if (roots.Count == 0) return Unreadable();
            var pages = new List<LegacyBrowserSearchSemantics.Node>();
            var complete = true;
            foreach (var root in roots)
            {
                var queue = new Queue<(IAccessible Node, int Depth)>();
                queue.Enqueue((root.Accessible, 0));
                var visited = 0;
                while (queue.Count > 0 && visited++ < 1500)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (accessible, depth) = queue.Dequeue();
                    var node = LegacyBrowserSearchSemantics.ReadNode(accessible, window);
                    if (node?.Attributes.GetValueOrDefault("id") == "page_pc_search_result")
                    {
                        pages.Add(node);
                        continue; // Never inspect unrelated account/sidebar contents or names.
                    }
                    if (node?.Attributes.GetValueOrDefault("id") is { } pageId
                        && pageId.StartsWith("page_pc_", StringComparison.Ordinal)) continue;
                    if (depth >= 14) { complete = false; continue; }
                    foreach (var child in LegacyBrowserSearchSemantics.Children(accessible)) queue.Enqueue((child, depth + 1));
                }
                complete &= queue.Count == 0;
            }
            if (pages.Count == 0) return new(complete, false, "", "", "", false, []);
            if (pages.Count != 1) return Unreadable();
            var page = pages[0];
            var direct = Nodes(page.Accessible, window);
            var labels = direct.Where(node => node.Role == 41).Select(Name).Where(name => name.Length > 0).ToArray();
            if (labels.Length != 1 || labels[0].Length > 200) return Unreadable();
            var scroll = direct.SingleOrDefault(node => node.Attributes.GetValueOrDefault("id") == "search-results-tab-content");
            if (scroll is null) return Unreadable();
            var main = Nodes(scroll.Accessible, window).SingleOrDefault(node => node.Attributes.GetValueOrDefault("tag") == "main");
            var result = main is null ? null : Nodes(main.Accessible, window)
                .SingleOrDefault(node => node.Attributes.GetValueOrDefault("id") == "page_pc_search_all");
            if (result is null) return new(true, true, labels[0], page.Identity, "", true, []);
            var busy = IsBusy(page) || IsBusy(scroll) || IsBusy(main!) || IsBusy(result);
            var children = Nodes(result.Accessible, window);
            var collecting = false;
            var songs = new List<SearchResultItem>();
            foreach (var child in children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                busy |= IsBusy(child);
                var inner = Nodes(child.Accessible, window);
                var headings = inner.Where(node => node.Attributes.GetValueOrDefault("tag") == "h2").ToArray();
                if (headings.Length > 0)
                {
                    if (collecting) break;
                    collecting = headings.Length == 1 && Name(headings[0]) == "单曲";
                    continue;
                }
                if (!collecting) continue;
                if (!ClassTokens(child).Contains("tapable", StringComparer.Ordinal)) return Unreadable();
                var title = ReadSongTitle(inner.Select(node => (node.Role, node.Attributes.GetValueOrDefault("display"),
                    node.Role == 41 ? Name(node) : "")));
                if (title is null || (child.State & (0x8000 | 0x10000)) != 0)
                    return Unreadable();
                busy |= inner.Any(IsBusy);
                // The native node identifies a result card, not the media resource. No Invoke token is supplied.
                songs.Add(new(child.Identity, title, new NativeResultReceipt(child.Identity)));
            }
            var generation = ComputeGeneration(result.Identity, songs);
            return new(true, true, labels[0], page.Identity, generation, busy, songs);
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or ArgumentException or InvalidCastException)
        { return Unreadable(); }
    }

    internal static bool IsVerifiedResultForQuery(NativeSearchPageObservation before, NativeSearchPageObservation after, string query) =>
        before.Readable && after.Readable && after.PageExists && after.Query == query && !after.Busy && after.Songs.Count > 0
        && (!before.PageExists || before.Query == query
            || (before.Generation != after.Generation
                && !before.Songs.Select(song => song.Title).SequenceEqual(after.Songs.Select(song => song.Title), StringComparer.Ordinal)));

    internal static string ComputeGeneration(string resultIdentity, IReadOnlyList<SearchResultItem> songs) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(resultIdentity + "\n"
            + string.Join("\n", songs.Select(song => song.Identity + "\t" + song.Title)))));

    // The observed card's block text is its title; inline text also contains author separators.
    internal static string? ReadSongTitle(IEnumerable<(int Role, string? Display, string Text)> parts)
    {
        var titles = parts.Where(part => part.Role == 41 && part.Display == "block")
            .Select(part => part.Text.Trim()).Where(text => text.Length > 0).ToArray();
        return titles.Length == 1 && titles[0].Length <= 200 ? titles[0] : null;
    }

    private static bool IsBusy(LegacyBrowserSearchSemantics.Node node) => (node.State & 0x800) != 0
        || node.Attributes.GetValueOrDefault("busy") == "true"
        || ClassTokens(node).Any(token => token.Contains("skeleton", StringComparison.OrdinalIgnoreCase)
            || token.Contains("loading", StringComparison.OrdinalIgnoreCase));
    private static string[] ClassTokens(LegacyBrowserSearchSemantics.Node node) =>
        (node.Attributes.GetValueOrDefault("class") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
    private static LegacyBrowserSearchSemantics.Node[] Nodes(IAccessible parent, long window) =>
        LegacyBrowserSearchSemantics.Children(parent).Select(child => LegacyBrowserSearchSemantics.ReadNode(child, window))
            .Where(node => node is not null).Cast<LegacyBrowserSearchSemantics.Node>().ToArray();
    private static string Name(LegacyBrowserSearchSemantics.Node node) => node.Accessible.get_accName(0)?.Trim() ?? "";
    private static NativeSearchPageObservation Unreadable() => new(false, false, "", "", "", false, []);
    private sealed record NativeResultReceipt(string Identity);
}
