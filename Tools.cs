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
        foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            if (IsProbablyBinary(file))
            {
                continue;
            }

            int lineNo = 0;
            foreach (string line in File.ReadLines(file))
            {
                lineNo++;
                if (line.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    hits.AppendLine($"{Path.GetRelativePath(_root, file).Replace('\\', '/')}:{lineNo}: {line.Trim()}");
                    if (++matches >= 100)
                    {
                        hits.AppendLine("... [stopped at 100 matches]");
                        return hits.ToString();
                    }
                }
            }
        }

        return matches == 0 ? $"No matches for '{pattern}'." : hits.ToString();
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

    private static bool IsProbablyBinary(string file)
    {
        string ext = Path.GetExtension(file).ToLowerInvariant();
        return ext is ".dll" or ".exe" or ".pdb" or ".png" or ".jpg" or ".jpeg"
            or ".gif" or ".ico" or ".zip" or ".gz" or ".pdf" or ".docx" or ".xlsx";
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
    ];

    #endregion
}
