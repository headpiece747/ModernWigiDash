namespace ModernWigiDash.Widgets.Todo;

using System.Text.Json;
using ModernWigiDash.Sdk;

/// <summary>
/// Thread-safe local file store for companion task widget state.
/// Writes atomically via temporary files into UserStateDirectory.Resolve() / "widgets" / state file.
/// </summary>
public sealed class TodoStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private static readonly Lock ProcessGate = new();
    private readonly string _filePath;

    /// <summary>
    /// Initializes a new instance of <see cref="TodoStore"/>.
    /// </summary>
    /// <param name="customPath">Optional custom path for testing isolation.</param>
    public TodoStore(string? customPath = null)
    {
        _filePath = customPath ?? Path.Combine(UserStateDirectory.Resolve(), "widgets", "cozy_todo_state.json");
    }

    /// <summary>The resolved path to the state file.</summary>
    public string FilePath => _filePath;

    /// <summary>Whether the persisted state file exists on disk.</summary>
    public bool Exists => File.Exists(_filePath);

    /// <summary>
    /// Seeds the starter state into a first-run store (the state file is
    /// absent): the sample tasks a first-time user sees. A no-op when the
    /// file already exists (a restored store keeps its own tasks).
    /// </summary>
    public void SeedStarterState()
    {
        if (Exists) return;
        Write(new TodoDataState
        {
            Tasks =
            [
                new TodoItem(Guid.NewGuid().ToString("N"), "Explore ModernWigiDash", true, "Setup"),
                new TodoItem(Guid.NewGuid().ToString("N"), "Feed desktop companion", false, "Pet"),
                new TodoItem(Guid.NewGuid().ToString("N"), "Complete 25m focus sprint", false, "Focus"),
            ]
        });
    }

    /// <summary>
    /// Reads and deserializes the stored task state from disk.
    /// Returns default empty state if file is absent or malformed.
    /// </summary>
    public TodoDataState Read()
    {
        lock (ProcessGate)
        {
            if (!File.Exists(_filePath))
                return new TodoDataState();

            try
            {
                byte[] bytes = File.ReadAllBytes(_filePath);
                return JsonSerializer.Deserialize<TodoDataState>(bytes, JsonOptions) ?? new TodoDataState();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // A mid-read fault (a racing write, a vanished file): the
                // default state is the truthful fallback; the next write
                // republishes the full state. The write leg is atomic
                // (temp file + move under the same gate), so an in-process
                // reader never sees a partial file.
                return new TodoDataState();
            }
        }
    }

    /// <summary>
    /// Atomically persists the task state to disk using a temporary file swap.
    /// </summary>
    public void Write(TodoDataState state)
    {
        lock (ProcessGate)
        {
            string? dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
            string tempFile = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(tempFile, bytes);
                File.Move(tempFile, _filePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    try
                    {
                        File.Delete(tempFile);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Best-effort cleanup of temporary file
                    }
                }
            }
        }
    }
}
