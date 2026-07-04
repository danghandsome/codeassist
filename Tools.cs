using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Anthropic.Models.Messages;

namespace CodeAssistantCli;

/// <summary>
/// Client-side tools the assistant can call. Every file operation is confined
/// to <see cref="_root"/> — model-supplied paths that escape the root are rejected.
/// </summary>
internal sealed class WorkspaceTools
{
    private readonly string _root;

    internal WorkspaceTools(string root)
    {
        _root = Path.GetFullPath(root);
    }

    /// <summary>JSON-schema tool definitions handed to the Messages API.</summary>
    internal static IReadOnlyList<Tool> Definitions { get; } = BuildDefinitions();

    /// <summary>Dispatch a tool call by name. Returns text fed back as a tool_result.</summary>
    internal string Execute(string name, IReadOnlyDictionary<string, JsonElement> input)
    {
        try
        {
            return name switch
            {
                "list_files" => ListFiles(GetString(input, "path", ".")),
                "read_file" => ReadFile(GetString(input, "path")),
                "write_file" => WriteFile(GetString(input, "path"), GetString(input, "content")),
                "search" => Search(GetString(input, "pattern"), GetString(input, "path", ".")),
                "run_command" => RunCommand(GetString(input, "command")),
                "git" => Git(GetString(input, "args")),
                _ => $"Error: unknown tool '{name}'.",
            };
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    #region Tool implementations

    private string ListFiles(string relative)
    {
        string dir = Resolve(relative);
        if (!Directory.Exists(dir))
        {
            return $"Error: directory not found: {relative}";
        }

        var sb = new StringBuilder();
        foreach (string entry in Directory.EnumerateFileSystemEntries(dir).OrderBy(e => e))
        {
            bool isDir = Directory.Exists(entry);
            sb.Append(isDir ? "[dir]  " : "[file] ");
            sb.AppendLine(Path.GetRelativePath(_root, entry).Replace('\\', '/'));
        }

        return sb.Length == 0 ? "(empty directory)" : sb.ToString();
    }

    private string ReadFile(string relative)
    {
        string file = Resolve(relative);
        if (!File.Exists(file))
        {
            return $"Error: file not found: {relative}";
        }

        string text = File.ReadAllText(file);
        // Guard against dumping a huge file into the context window.
        const int maxChars = 40_000;
        return text.Length > maxChars
            ? text[..maxChars] + $"\n\n... [truncated at {maxChars} chars]"
            : text;
    }

    private string WriteFile(string relative, string content)
    {
        string file = Resolve(relative);
        string? parent = Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        File.WriteAllText(file, content);
        return $"Wrote {content.Length} chars to {relative}.";
    }

    private string Search(string pattern, string relative)
    {
        string dir = Resolve(relative);
        if (!Directory.Exists(dir))
        {
            return $"Error: directory not found: {relative}";
        }

        var hits = new StringBuilder();
        int matches = 0;
        foreach (string file in EnumerateSearchableFiles(dir))
        {
            if (ShouldSkipForSearch(file))
            {
                continue;
            }

            int lineNo = 0;
            foreach (string line in File.ReadLines(file))
            {
                lineNo++;
                if (line.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    // A single minified line can be an entire file — cap its length.
                    string snippet = line.Trim();
                    if (snippet.Length > MaxLineLength)
                    {
                        snippet = snippet[..MaxLineLength] + " …";
                    }

                    hits.AppendLine($"{Path.GetRelativePath(_root, file).Replace('\\', '/')}:{lineNo}: {snippet}");

                    // Bound by both match count and total size so a big vendor tree
                    // can never overflow the model's context window.
                    if (++matches >= MaxMatches || hits.Length >= MaxResultChars)
                    {
                        hits.AppendLine("... [truncated]");
                        return hits.ToString();
                    }
                }
            }
        }

        return matches == 0 ? $"No matches for '{pattern}'." : hits.ToString();
    }

    // Only build/test runners are allowed — never arbitrary shell commands.
    private static readonly string[] AllowedCommands = { "dotnet", "npm", "node" };

    private string RunCommand(string command)
    {
        command = command.Trim();
        string exe = command.Split(' ', 2)[0];
        if (!AllowedCommands.Contains(exe))
        {
            return $"Error: command '{exe}' is not allowed. Allowed: {string.Join(", ", AllowedCommands)}.";
        }

        string args = command.Length > exe.Length ? command[exe.Length..].Trim() : string.Empty;
        return RunProcess(exe, args);
    }

    // Read + commit git subcommands only — no push/reset/clean.
    private static readonly string[] AllowedGit = { "status", "diff", "log", "show", "branch", "add", "commit" };

    private string Git(string args)
    {
        args = args.Trim();
        string sub = args.Split(' ', 2)[0];
        if (!AllowedGit.Contains(sub))
        {
            return $"Error: git '{sub}' is not allowed. Allowed: {string.Join(", ", AllowedGit)}.";
        }

        return RunProcess("git", args);
    }

    private string RunProcess(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = _root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using Process? p = Process.Start(psi);
            if (p is null)
            {
                return "Error: failed to start process.";
            }

            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(60_000))
            {
                try { p.Kill(true); } catch { /* ignore */ }
                return "Error: command timed out (60s).";
            }

            string output = (stdout + (stderr.Length > 0 ? "\n" + stderr : string.Empty)).Trim();
            const int max = 12_000;
            if (output.Length > max)
            {
                output = output[..max] + "\n... [truncated]";
            }

            return output.Length == 0 ? $"(exit {p.ExitCode}, no output)" : $"[exit {p.ExitCode}]\n{output}";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    #endregion

    #region Helpers

    /// <summary>Resolve a model-supplied path and reject anything outside the root.</summary>
    private string Resolve(string relative)
    {
        string full = Path.GetFullPath(Path.Combine(_root, relative));
        if (!full.Equals(_root, StringComparison.OrdinalIgnoreCase)
            && !full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException($"path '{relative}' escapes the workspace root.");
        }

        return full;
    }

    // Directories that never hold useful source — pruned by search so vendored
    // dependencies and build output can't overflow the context window.
    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "bin", "obj", "packages",
        "node_modules", "dist", "build", ".next", "vendor",
    };

    private const int MaxMatches = 100;
    private const int MaxResultChars = 8_000;
    private const int MaxLineLength = 240;
    private const long MaxSearchFileBytes = 1_000_000;

    /// <summary>Walk files under <paramref name="root"/>, pruning ignored directories.</summary>
    private static IEnumerable<string> EnumerateSearchableFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();

            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch { continue; }

            foreach (string sub in subdirs)
            {
                if (!IgnoredDirs.Contains(Path.GetFileName(sub)))
                {
                    stack.Push(sub);
                }
            }

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { continue; }

            foreach (string file in files)
            {
                yield return file;
            }
        }
    }

    /// <summary>Skip binaries, minified bundles, source maps and oversized files.</summary>
    private static bool ShouldSkipForSearch(string file)
    {
        if (IsProbablyBinary(file))
        {
            return true;
        }

        string name = Path.GetFileName(file).ToLowerInvariant();
        if (name.Contains(".min.") || name.EndsWith(".map"))
        {
            return true;
        }

        try
        {
            if (new FileInfo(file).Length > MaxSearchFileBytes)
            {
                return true;
            }
        }
        catch { /* ignore unreadable file */ }

        return false;
    }

    private static bool IsProbablyBinary(string file)
    {
        string ext = Path.GetExtension(file).ToLowerInvariant();
        return ext is ".dll" or ".exe" or ".pdb" or ".png" or ".jpg" or ".jpeg"
            or ".gif" or ".ico" or ".zip" or ".gz" or ".pdf" or ".docx" or ".xlsx"
            or ".mp4" or ".webm" or ".mov" or ".mp3" or ".wav"
            or ".woff" or ".woff2" or ".ttf" or ".eot" or ".svg" or ".psd" or ".bmp";
    }

    private static string GetString(IReadOnlyDictionary<string, JsonElement> input, string key, string? fallback = null)
    {
        if (input.TryGetValue(key, out JsonElement value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? fallback ?? string.Empty;
        }

        return fallback ?? throw new ArgumentException($"missing required argument '{key}'.");
    }

    private static Tool Def(string name, string description, object properties, string[] required) => new()
    {
        Name = name,
        Description = description,
        InputSchema = new()
        {
            Properties = ((Dictionary<string, object>)properties)
                .ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value)),
            Required = required,
        },
    };

    private static IReadOnlyList<Tool> BuildDefinitions() =>
    [
        Def("list_files", "List files and directories under a path (relative to the workspace root).",
            new Dictionary<string, object>
            {
                ["path"] = new { type = "string", description = "Directory path, relative to the workspace. Defaults to '.'." },
            },
            []),

        Def("read_file", "Read the full text content of a file.",
            new Dictionary<string, object>
            {
                ["path"] = new { type = "string", description = "File path relative to the workspace." },
            },
            ["path"]),

        Def("write_file", "Create or overwrite a file with the given content.",
            new Dictionary<string, object>
            {
                ["path"] = new { type = "string", description = "File path relative to the workspace." },
                ["content"] = new { type = "string", description = "Full file content to write." },
            },
            ["path", "content"]),

        Def("search", "Search recursively for a text pattern across files (case-insensitive).",
            new Dictionary<string, object>
            {
                ["pattern"] = new { type = "string", description = "Text to search for." },
                ["path"] = new { type = "string", description = "Directory to search under. Defaults to '.'." },
            },
            ["pattern"]),

        Def("run_command", "Run an allowed build/test command in the workspace (dotnet, npm, node). Use it to compile or run tests and read the result.",
            new Dictionary<string, object>
            {
                ["command"] = new { type = "string", description = "Full command, e.g. 'dotnet build' or 'dotnet test'." },
            },
            ["command"]),

        Def("git", "Run a read or commit git command in the workspace (status, diff, log, show, branch, add, commit).",
            new Dictionary<string, object>
            {
                ["args"] = new { type = "string", description = "Git arguments, e.g. 'diff', 'log --oneline -5', or 'commit -m \"message\"'." },
            },
            ["args"]),
    ];

    #endregion
}
