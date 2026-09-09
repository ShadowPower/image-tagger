using System.Text;
using Xunit;

namespace ImageTagger.Tests.TestInfrastructure;

/// <summary>
/// Detects Git LFS pointer files (a checkout without "git lfs pull") and turns
/// them into explicit skips so a pointer is never fed to a decoder as if it
/// were a real asset.
/// </summary>
public static class LfsAssets
{
    private const string PointerSignature = "version https://git-lfs";

    /// <summary>True when the file content is an LFS pointer rather than the real object.</summary>
    public static bool IsGitLfsPointer(string path)
    {
        if (!File.Exists(path))
            return false;
        using var stream = File.OpenRead(path);
        Span<byte> buffer = stackalloc byte[64];
        var read = stream.Read(buffer);
        return Encoding.ASCII.GetString(buffer[..read])
            .StartsWith(PointerSignature, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns <paramref name="path"/> when a real (non-pointer, non-empty)
    /// file exists; otherwise skips the calling test with a message that says
    /// exactly which LFS asset is missing and how to fetch it.
    /// </summary>
    public static string RequireRealFile(string path, string purpose)
    {
        if (!File.Exists(path))
            Assert.Skip($"Missing LFS asset for {purpose}: {path} does not exist. " +
                        "Run 'git lfs install && git lfs pull' and re-run the RealModel suite.");
        if (IsGitLfsPointer(path))
            Assert.Skip($"Missing LFS asset for {purpose}: {path} is still a Git LFS pointer. " +
                        "Run 'git lfs install && git lfs pull' and re-run the RealModel suite.");
        return path;
    }
}
