namespace ScreenGuide.Skills.Windows.Tests;

public sealed class DesktopFileCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "desktop-file-test-" + Guid.NewGuid().ToString("N"));
    public DesktopFileCatalogTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void ExactAndExtensionlessNamesResolveWithoutReadingOrLaunching()
    {
        File.WriteAllText(Path.Combine(_root, "年度计划.md"), "fixture");
        var catalog = new DesktopFileCatalog([_root]);
        var exact = catalog.Resolve("年度计划.md").File;
        Assert.NotNull(exact);
        Assert.Equal(exact, catalog.Resolve("年度计划").File);
        catalog.RequireUnchanged(exact);
        Assert.Null(catalog.Resolve("年度").File);
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("..\\secret.txt")]
    [InlineData("note.txt:stream")]
    [InlineData("*.txt")]
    [InlineData("run.exe")]
    [InlineData("link.lnk")]
    public void UnsafeNamesAndExecutableTypesNeverResolve(string name)
    {
        File.WriteAllText(Path.Combine(_root, "run.exe"), "fixture");
        File.WriteAllText(Path.Combine(_root, "link.lnk"), "fixture");
        Assert.Null(new DesktopFileCatalog([_root]).Resolve(name).File);
    }

    [Fact]
    public void DuplicateNamesAndNestedFilesAreNotGuessed()
    {
        var second = Path.Combine(_root, "public"); Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(_root, "计划.txt"), "first");
        File.WriteAllText(Path.Combine(second, "计划.txt"), "second");
        Assert.Null(new DesktopFileCatalog([_root, second]).Resolve("计划.txt").File);
        Assert.Null(new DesktopFileCatalog([_root]).Resolve("public/计划.txt").File);
        File.WriteAllText(Path.Combine(_root, "计划.md"), "third");
        Assert.Null(new DesktopFileCatalog([_root]).Resolve("计划").File);
    }

    [Fact]
    public void ReplacedOrLinkedFileFailsBeforeExecution()
    {
        var path = Path.Combine(_root, "计划.txt"); File.WriteAllText(path, "first");
        var catalog = new DesktopFileCatalog([_root]); var binding = catalog.Resolve("计划.txt").File!;
        File.WriteAllText(path, "changed content");
        Assert.Throws<InvalidOperationException>(() => catalog.RequireUnchanged(binding));
        var linked = Path.Combine(_root, "linked.txt"); File.CreateSymbolicLink(linked, path);
        Assert.Null(catalog.Resolve("linked.txt").File);
    }

    [Fact]
    public void PartialNameFindReturnsBoundDocumentsWithoutReadingTheirContents()
    {
        var path = Path.Combine(_root, "项目报告最终版.txt");
        File.WriteAllText(path, "private fixture must not be read");
        using var lockedContent = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var catalog = new DesktopFileCatalog([_root]);

        var found = catalog.Find("项目报告");

        Assert.True(found.Complete);
        Assert.False(found.ExactNameFound);
        Assert.Equal(1, found.TotalMatches);
        Assert.Equal(path, Assert.Single(found.Candidates).FullPath);
        Assert.Null(catalog.Resolve("项目报告").File);
    }

    [Fact]
    public void CompleteNameTakesPrecedenceOverPartialMatches()
    {
        var exact = Path.Combine(_root, "报告.md");
        File.WriteAllText(exact, "exact");
        File.WriteAllText(Path.Combine(_root, "年度报告.md"), "partial");
        var catalog = new DesktopFileCatalog([_root]);

        foreach (var query in new[] { "报告", "报告.md" })
        {
            var found = catalog.Find(query);
            Assert.True(found.Complete);
            Assert.True(found.ExactNameFound);
            Assert.Equal(1, found.TotalMatches);
            Assert.Equal(exact, Assert.Single(found.Candidates).FullPath);
            Assert.Null(catalog.BindSelected(query, Path.Combine(_root, "年度报告.md")).File);
        }
    }

    [Theory]
    [InlineData("run.exe")]
    [InlineData("run.lnk")]
    [InlineData("run.url")]
    public void UnsafeExactItemCannotFallThroughToSafePartialDocument(string name)
    {
        File.WriteAllText(Path.Combine(_root, name), "unsafe fixture");
        var partial = Path.Combine(_root, name + "说明.txt");
        File.WriteAllText(partial, "safe fixture");
        var catalog = new DesktopFileCatalog([_root]);

        var found = catalog.Find(name);

        Assert.True(found.Complete);
        Assert.True(found.ExactNameFound);
        Assert.Equal(0, found.TotalMatches);
        Assert.Empty(found.Candidates);
        Assert.Null(catalog.BindSelected(name, partial).File);
    }

    [Fact]
    public void SearchHasEightDisplayCandidatesButCountsAndCanBindAllMatches()
    {
        for (var index = 0; index < 12; index++)
            File.WriteAllText(Path.Combine(_root, $"项目报告-{index:00}.txt"), "fixture");
        var catalog = new DesktopFileCatalog([_root]);

        var found = catalog.Find("项目报告");

        Assert.True(found.Complete);
        Assert.Equal(12, found.TotalMatches);
        Assert.Equal(8, found.Candidates.Count);
        Assert.Equal("项目报告-00.txt", Path.GetFileName(found.Candidates[0].FullPath));
        Assert.Equal("项目报告-07.txt", Path.GetFileName(found.Candidates[^1].FullPath));
        var selected = catalog.BindSelected("项目报告", Path.Combine(_root, "项目报告-11.txt"));
        Assert.NotNull(selected.File);
        catalog.RequireUnchanged(selected.File);
    }

    [Fact]
    public void MatchingIsCaseInsensitiveAndDuplicateRootPathsAreNotCountedTwice()
    {
        var path = Path.Combine(_root, "Project-REPORT-final.PDF");
        File.WriteAllText(path, "fixture");
        var found = new DesktopFileCatalog([_root, _root]).Find("report");

        Assert.True(found.Complete);
        Assert.Equal(1, found.TotalMatches);
        Assert.Equal(path, Assert.Single(found.Candidates).FullPath);
    }

    [Fact]
    public void OnlyFirstLayerDocumentsAreCandidatesAndCanBeSelected()
    {
        var nested = Path.Combine(_root, "nested");
        Directory.CreateDirectory(nested);
        var nestedPath = Path.Combine(nested, "项目报告.txt");
        File.WriteAllText(nestedPath, "nested fixture");
        File.WriteAllText(Path.Combine(_root, "项目报告.exe"), "executable fixture");
        File.WriteAllText(Path.Combine(_root, "项目报告.lnk"), "shortcut fixture");
        var linked = Path.Combine(_root, "项目报告链接.txt");
        File.CreateSymbolicLink(linked, nestedPath);
        var safe = Path.Combine(_root, "项目报告正文.txt");
        File.WriteAllText(safe, "document fixture");
        var catalog = new DesktopFileCatalog([_root]);

        var found = catalog.Find("项目报");

        Assert.True(found.Complete);
        Assert.Equal(safe, Assert.Single(found.Candidates).FullPath);
        Assert.Null(catalog.BindSelected("项目报", nestedPath).File);
        Assert.Null(catalog.BindSelected("项目报", linked).File);
        Assert.Null(catalog.BindSelected("项目报", Path.Combine(_root, "项目报告.exe")).File);
        Assert.Null(catalog.BindSelected("不匹配", safe).File);
    }

    [Fact]
    public void SearchAcrossConfiguredDesktopRootsDoesNotGuessBetweenSameNames()
    {
        var second = Path.Combine(_root, "public");
        Directory.CreateDirectory(second);
        var firstPath = Path.Combine(_root, "报告.txt");
        var secondPath = Path.Combine(second, "报告.txt");
        File.WriteAllText(firstPath, "first");
        File.WriteAllText(secondPath, "second");
        var catalog = new DesktopFileCatalog([_root, second]);

        var found = catalog.Find("报告");

        Assert.True(found.Complete);
        Assert.True(found.ExactNameFound);
        Assert.Equal(2, found.TotalMatches);
        Assert.Equal(secondPath, catalog.BindSelected("报告", secondPath).File?.FullPath);
    }

    [Fact]
    public void IncompleteEnumerationCannotReturnAnApparentlyUniqueMatch()
    {
        File.WriteAllText(Path.Combine(_root, "唯一报告.txt"), "fixture");
        for (var index = 0; index < 2000; index++)
            File.WriteAllText(Path.Combine(_root, $"filler-{index}.txt"), "fixture");
        var catalog = new DesktopFileCatalog([_root]);

        var found = catalog.Find("唯一报告");

        Assert.False(found.Complete);
        Assert.Empty(found.Candidates);
        Assert.Equal(0, found.TotalMatches);
        Assert.Null(catalog.BindSelected("唯一报告", Path.Combine(_root, "唯一报告.txt")).File);
    }

    [Fact]
    public void SelectedPartialNameDocumentStillRejectsReplacementBeforeExecution()
    {
        var path = Path.Combine(_root, "项目报告最终版.txt");
        File.WriteAllText(path, "first");
        var catalog = new DesktopFileCatalog([_root]);
        var selected = catalog.BindSelected("项目报告", path).File;
        Assert.NotNull(selected);

        File.WriteAllText(path, "changed after selection");

        Assert.Throws<InvalidOperationException>(() => catalog.RequireUnchanged(selected));
    }

    [Fact]
    public void NoMatchesIsACompleteReadOnlySearch()
    {
        var found = new DesktopFileCatalog([_root]).Find("没有这个文件");
        Assert.True(found.Complete);
        Assert.False(found.ExactNameFound);
        Assert.Empty(found.Candidates);
        Assert.Equal(0, found.TotalMatches);
    }

    [Fact]
    public void ChangedDesktopRootCannotBeSilentlySkippedToClaimUniqueMatch()
    {
        var changedRoot = Path.Combine(_root, "desktop-was-replaced");
        File.WriteAllText(changedRoot, "not a directory");
        File.WriteAllText(Path.Combine(_root, "项目报告.txt"), "fixture");
        var catalog = new DesktopFileCatalog([_root, changedRoot]);

        var found = catalog.Find("项目报告");

        Assert.False(found.Complete);
        Assert.Empty(found.Candidates);
        Assert.Null(catalog.BindSelected("项目报告", Path.Combine(_root, "项目报告.txt")).File);
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("报告\n")]
    [InlineData("../报告")]
    [InlineData("..\\报告")]
    [InlineData("报告:stream")]
    [InlineData("*.txt")]
    public void InvalidSearchNamesCannotProduceCandidatesOrSelection(string name)
    {
        var path = Path.Combine(_root, "报告.txt");
        File.WriteAllText(path, "fixture");
        var catalog = new DesktopFileCatalog([_root]);
        Assert.False(catalog.Find(name).Complete);
        Assert.Empty(catalog.Find(name).Candidates);
        Assert.Null(catalog.BindSelected(name, path).File);
    }
}
