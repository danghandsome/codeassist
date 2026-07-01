using System.Text.Json;
using Anthropic.Models.Messages;

namespace CodeAssistantCli;

/// <summary>
/// Persists a lightweight conversation transcript (role + text) to a JSON file
/// in the workspace, so the assistant remembers prior sessions. Tool-call blocks
/// are transient and not persisted — only the readable turns.
/// </summary>
internal sealed class ConversationMemory
{
    private readonly string _file;
    private readonly List<Turn> _turns = [];

    internal ConversationMemory(string workspaceRoot)
    {
        _file = Path.Combine(workspaceRoot, ".codeassist-history.json");
        Load();
    }

    internal int Count => _turns.Count;

    /// <summary>Seed the live message history with prior turns as plain text.</summary>
    internal void SeedInto(List<MessageParam> history)
    {
        foreach (Turn t in _turns)
        {
            history.Add(new MessageParam
            {
                Role = t.Role == "user" ? Role.User : Role.Assistant,
                Content = t.Text,
            });
        }
    }

    internal void Record(string role, string text)
    {
        _turns.Add(new Turn { Role = role, Text = text });
        Save();
    }

    internal void Clear()
    {
        _turns.Clear();
        try { if (File.Exists(_file)) File.Delete(_file); } catch { /* ignore */ }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;
            var loaded = JsonSerializer.Deserialize<List<Turn>>(File.ReadAllText(_file));
            if (loaded is not null) _turns.AddRange(loaded);
        }
        catch { /* ignore corrupt history */ }
    }

    private void Save()
    {
        try { File.WriteAllText(_file, JsonSerializer.Serialize(_turns)); } catch { /* ignore */ }
    }

    private sealed class Turn
    {
        public string Role { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
    }
}
