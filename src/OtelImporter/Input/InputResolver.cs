namespace OtelImporter.Input;

internal sealed record InputResolution(IReadOnlyList<string> Files, string? Error)
{
    public static InputResolution Resolved(IReadOnlyList<string> files) => new(files, null);
    public static InputResolution Failed(string error) => new([], error);
}

// Resolves the positional input path to an ordered list of trace files.
//
//   * a path to a regular file yields just that file (any extension -- the stream
//     factory sniffs the actual format, so an explicitly named file is always honoured);
//   * a path to a directory yields every *.jsonl / *.jsonl.zst / *.json file directly
//     inside it, sorted by name for deterministic ordering. Subdirectories are not searched.
//
// A missing path, or a directory with no matching files, is reported as an error.
internal static class InputResolver
{
    static readonly string[] TracePatterns = ["*.jsonl", "*.jsonl.zst", "*.json"];

    public static InputResolution Resolve(string path)
    {
        // A named file wins, regardless of extension: matches the previous behaviour
        // where the positional argument pointed straight at one file.
        if (File.Exists(path))
            return InputResolution.Resolved([path]);

        if (Directory.Exists(path))
        {
            var files = TracePatterns
                .SelectMany(pattern => Directory.EnumerateFiles(path, pattern, SearchOption.TopDirectoryOnly))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(file => file, StringComparer.Ordinal)
                .ToList();

            if (files.Count == 0)
                return InputResolution.Failed($"no .jsonl, .jsonl.zst or .json files found in directory: {path}");

            return InputResolution.Resolved(files);
        }

        return InputResolution.Failed($"input path not found: {path}");
    }
}
