namespace ImageTagger.Tests.TestInfrastructure;

/// <summary>
/// Unique temporary directory removed on dispose; keeps tests from sharing or
/// polluting repo folders.
/// </summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory(string? hint = null)
    {
        var name = string.IsNullOrEmpty(hint)
            ? $"imagetagger-test-{Guid.NewGuid():N}"
            : $"imagetagger-test-{hint}-{Guid.NewGuid():N}";
        FullPath = Path.Combine(Path.GetTempPath(), name);
        Directory.CreateDirectory(FullPath);
    }

    public string FullPath { get; }

    public string CreateSubdirectory(string relative)
    {
        var path = Path.Combine(FullPath, relative);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Writes a file (creating parent directories) and returns its full path.</summary>
    public string WriteFile(string relative, byte[] content)
    {
        var path = Path.Combine(FullPath, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(FullPath, recursive: true);
        }
        catch (IOException)
        {
            // best effort; leftover temp dirs are harmless
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
