using OtelImporter.Input;

namespace OtelImporter.Tests;

public class InputResolverTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("otelimporter-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    string Touch(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void ResolvesASingleFileToItself()
    {
        var file = Touch("traces.jsonl");

        var result = InputResolver.Resolve(file);

        Assert.Null(result.Error);
        Assert.Equal([file], result.Files);
    }

    [Fact]
    public void ResolvesANamedFileRegardlessOfExtension()
    {
        // A file pointed at directly is always honoured; the stream factory sniffs format.
        var file = Touch("traces.txt");

        var result = InputResolver.Resolve(file);

        Assert.Null(result.Error);
        Assert.Equal([file], result.Files);
    }

    [Fact]
    public void ResolvesADirectoryToItsTraceFilesInNameOrder()
    {
        var b = Touch("b.jsonl");
        var a = Touch("a.jsonl.zst");
        var c = Touch("c.jsonl");

        var result = InputResolver.Resolve(_dir);

        Assert.Null(result.Error);
        Assert.Equal([a, b, c], result.Files);
    }

    [Fact]
    public void ResolvesJsonFilesInADirectory()
    {
        var a = Touch("a.json");
        var b = Touch("b.jsonl");

        var result = InputResolver.Resolve(_dir);

        Assert.Null(result.Error);
        Assert.Equal([a, b], result.Files);
    }

    [Fact]
    public void IgnoresNonTraceFilesInADirectory()
    {
        var traces = Touch("traces.jsonl");
        Touch("README.md");
        Touch("notes.txt");

        var result = InputResolver.Resolve(_dir);

        Assert.Null(result.Error);
        Assert.Equal([traces], result.Files);
    }

    [Fact]
    public void DoesNotSearchSubdirectories()
    {
        var top = Touch("top.jsonl");
        var nested = Directory.CreateDirectory(Path.Combine(_dir, "nested"));
        File.WriteAllText(Path.Combine(nested.FullName, "deep.jsonl"), "");

        var result = InputResolver.Resolve(_dir);

        Assert.Null(result.Error);
        Assert.Equal([top], result.Files);
    }

    [Fact]
    public void FailsWhenDirectoryHasNoTraceFiles()
    {
        Touch("README.md");

        var result = InputResolver.Resolve(_dir);

        Assert.NotNull(result.Error);
        Assert.Empty(result.Files);
    }

    [Fact]
    public void SameFilenameInDifferentDirectoriesAreNotDeduplicated()
    {
        // Dedup in Program.cs uses the full path, so dir1/file.jsonl and dir2/file.jsonl
        // are distinct even though the filename is the same.
        var dir1 = Directory.CreateDirectory(Path.Combine(_dir, "dir1")).FullName;
        var dir2 = Directory.CreateDirectory(Path.Combine(_dir, "dir2")).FullName;
        var file1 = Path.Combine(dir1, "traces.jsonl");
        var file2 = Path.Combine(dir2, "traces.jsonl");
        File.WriteAllText(file1, "");
        File.WriteAllText(file2, "");

        var allFiles = new List<string>();
        allFiles.AddRange(InputResolver.Resolve(dir1).Files);
        allFiles.AddRange(InputResolver.Resolve(dir2).Files);
        var deduped = allFiles.Distinct(StringComparer.Ordinal).ToList();

        Assert.Equal([file1, file2], deduped);
    }

    [Fact]
    public void FailsWhenPathDoesNotExist()
    {
        var result = InputResolver.Resolve(Path.Combine(_dir, "missing.jsonl"));

        Assert.NotNull(result.Error);
        Assert.Empty(result.Files);
    }
}
