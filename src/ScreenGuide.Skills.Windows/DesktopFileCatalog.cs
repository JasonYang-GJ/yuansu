using System.IO;

namespace ScreenGuide.Skills.Windows;

public sealed record DesktopFileBinding(string FullPath, long Length, DateTime LastWriteTimeUtc, DateTime CreationTimeUtc);
public sealed record DesktopFileResolution(DesktopFileBinding? File, string Message);
public sealed record DesktopFileSearchResult(
    IReadOnlyList<DesktopFileBinding> Candidates,
    int TotalMatches,
    bool Complete,
    string Message,
    bool ExactNameFound = false);

public interface IDesktopFileCatalog
{
    DesktopFileResolution Resolve(string name);
    void RequireUnchanged(DesktopFileBinding binding);

    // Older catalog implementations can still resolve a single exact name, but must not
    // report an exhaustive partial-name search that they have not performed.
    DesktopFileSearchResult Find(string name)
    {
        var exact = Resolve(name);
        return exact.File is null
            ? new([], 0, false, exact.Message)
            : new([exact.File], 1, true, exact.Message, ExactNameFound: true);
    }

    DesktopFileResolution BindSelected(string name, string fullPath)
    {
        var exact = Resolve(name);
        if (exact.File is null || !string.Equals(exact.File.FullPath, fullPath, StringComparison.OrdinalIgnoreCase))
            return new(null, "所选文件不是这次找到的桌面文件，请重新选择。");
        RequireUnchanged(exact.File);
        return exact;
    }
}

/// <summary>Names in the two Windows desktop folders only. No recursion or file-content reads.</summary>
public sealed class DesktopFileCatalog : IDesktopFileCatalog
{
    private const int MaximumFilesPerRoot = 2000;
    private const int MaximumDisplayedMatches = 8;
    private readonly string[] _roots;
    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".pdf", ".docx", ".xlsx", ".pptx", ".csv", ".rtf",
        ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp",
        ".mp3", ".wav", ".flac", ".mp4", ".mkv", ".avi", ".mov", ".json", ".log"
    };

    public DesktopFileCatalog() : this([
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)]) { }

    internal DesktopFileCatalog(IEnumerable<string> roots) => _roots = roots
        .Where(root => !string.IsNullOrWhiteSpace(root)).Select(Path.GetFullPath)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public DesktopFileResolution Resolve(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || name.Any(char.IsControl)
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
            return new(null, "请说出完整的桌面文件名，或选择这次要打开的文件。");
        var candidates = new List<FileInfo>();
        try
        {
            foreach (var root in _roots)
            {
                if (!Directory.Exists(root)) continue;
                // Reject an incomplete enumeration instead of treating its first match as unique.
                var files = new DirectoryInfo(root).EnumerateFiles().Take(2001).ToArray();
                if (files.Length > 2000) return new(null, "桌面文件过多，请直接选择这次要打开的文件。");
                candidates.AddRange(files.Where(file =>
                    string.Equals(file.Name, name, StringComparison.OrdinalIgnoreCase)
                    || !Path.HasExtension(name) && string.Equals(Path.GetFileNameWithoutExtension(file.Name), name, StringComparison.OrdinalIgnoreCase)));
            }
            candidates = candidates.DistinctBy(file => file.FullName, StringComparer.OrdinalIgnoreCase).ToList();
            if (candidates.Count == 0) return new(null, "桌面上没有找到这个文件，请检查文件名或直接选择文件。");
            if (candidates.Count > 1) return new(null, "桌面上有多个同名文件，请选择这次要打开的具体文件。");
            var match = candidates[0];
            if (!IsDocument(match)) return new(null, "这个桌面项目是快捷方式、链接或不支持自动匹配的文件类型，请使用应用名称或文件选择入口。");
            return new(new(match.FullName, match.Length, match.LastWriteTimeUtc, match.CreationTimeUtc), "已找到唯一匹配的桌面文件。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(null, "无法完整检查桌面目录，请直接选择这次要打开的文件。");
        }
    }

    public DesktopFileSearchResult Find(string name)
    {
        var matches = ReadMatches(name);
        if (!matches.Complete) return new([], 0, false, matches.Message);
        try
        {
            var documents = matches.Files.Where(IsDocument)
                .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(file => file.FullName, StringComparer.OrdinalIgnoreCase).ToArray();
            var candidates = documents.Take(MaximumDisplayedMatches).Select(CreateBinding).ToArray();
            // Do not display or later authorize a candidate whose metadata changed while reading it.
            foreach (var candidate in candidates) RequireUnchanged(candidate);
            var message = documents.Length == 0
                ? matches.ExactNameFound
                    ? "找到的同名桌面项目是链接或不支持的文件类型，未打开。"
                    : "桌面上没有找到名称匹配的文档，未打开任何文件。"
                : documents.Length > MaximumDisplayedMatches
                    ? $"找到 {documents.Length} 个匹配的桌面文件，下面列出前 {MaximumDisplayedMatches} 个；尚未打开。"
                    : $"找到 {documents.Length} 个匹配的桌面文件，尚未打开。";
            return new(candidates, documents.Length, true, message, matches.ExactNameFound);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new([], 0, false, "桌面文件在查找时发生变化或无法检查，请重新查找。");
        }
    }

    public DesktopFileResolution BindSelected(string name, string fullPath)
    {
        var matches = ReadMatches(name);
        if (!matches.Complete) return new(null, matches.Message);
        try
        {
            // The file picker supplies a path, not authority to leave this request's desktop scope.
            var selectedPath = Path.GetFullPath(fullPath);
            var selected = matches.Files.SingleOrDefault(file =>
                string.Equals(file.FullName, selectedPath, StringComparison.OrdinalIgnoreCase));
            if (selected is null || !IsDocument(selected))
                return new(null, "所选文件不是这次找到的桌面文档，请选择桌面中名称匹配的具体文件。");
            var binding = CreateBinding(selected);
            RequireUnchanged(binding);
            return new(binding, "已选定匹配的桌面文件，确认后才会打开。");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return new(null, "所选桌面文件已经变化或不可用，请重新选择并确认。");
        }
    }

    private MatchSet ReadMatches(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || name.Any(char.IsControl)
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
            return new([], false, false, "请提供桌面文件名或其中一部分，不要输入路径、通配符或命令。");
        try
        {
            var allFiles = new List<FileInfo>();
            foreach (var root in _roots)
            {
                FileAttributes rootAttributes;
                try { rootAttributes = File.GetAttributes(root); }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                // Directory.Exists also returns false on access errors. Do not silently skip
                // an unreadable desktop and call a match in the other desktop unique.
                if ((rootAttributes & FileAttributes.Directory) == 0)
                    return new([], false, false, "桌面目录已经变化，无法完整查找，请重新选择文件。");
                var files = new DirectoryInfo(root).EnumerateFiles().Take(MaximumFilesPerRoot + 1).ToArray();
                if (files.Length > MaximumFilesPerRoot)
                    return new([], false, false, "桌面文件过多，无法完整查找，请缩小范围或使用文件选择入口。");
                allFiles.AddRange(files);
            }
            var distinct = allFiles.DistinctBy(file => file.FullName, StringComparer.OrdinalIgnoreCase).ToArray();
            var exact = distinct.Where(file => string.Equals(file.Name, name, StringComparison.OrdinalIgnoreCase)
                || !Path.HasExtension(name) && string.Equals(Path.GetFileNameWithoutExtension(file.Name), name,
                    StringComparison.OrdinalIgnoreCase)).ToArray();
            // Preserve an unsafe exact match as a boundary: “run.exe” must not fall through
            // to an unrelated safe document named “run.exe instructions.txt”.
            return exact.Length > 0
                ? new(exact, true, true, "")
                : new(distinct.Where(file => file.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).ToArray(),
                    true, false, "");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new([], false, false, "无法完整检查桌面目录，请稍后重新查找。");
        }
    }

    private static DesktopFileBinding CreateBinding(FileInfo file) =>
        new(file.FullName, file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc);

    private sealed record MatchSet(IReadOnlyList<FileInfo> Files, bool Complete, bool ExactNameFound, string Message);

    public void RequireUnchanged(DesktopFileBinding binding)
    {
        var file = new FileInfo(binding.FullPath);
        if (!_roots.Contains(file.DirectoryName, StringComparer.OrdinalIgnoreCase) || !IsDocument(file)
            || file.Length != binding.Length || file.LastWriteTimeUtc != binding.LastWriteTimeUtc
            || file.CreationTimeUtc != binding.CreationTimeUtc)
            throw new InvalidOperationException("桌面文件在确认后发生变化，请重新选择并确认，尚未打开。");
    }

    private static bool IsDocument(FileInfo file) => file.Exists
        && (file.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) == 0
        && DocumentExtensions.Contains(file.Extension);
}
