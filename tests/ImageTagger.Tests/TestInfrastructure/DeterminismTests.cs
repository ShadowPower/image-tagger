using ImageTagger.Tests.TestInfrastructure;
using Xunit;

namespace ImageTagger.Tests.TestInfrastructure;

/// <summary>Self-tests for the deterministic helpers other suites rely on.</summary>
public class DeterminismTests
{
    [Fact]
    public async Task WaitForAsync_returns_immediately_when_condition_holds()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Determinism.WaitForAsync(() => true, description: "always true");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task WaitForAsync_times_out_with_description()
    {
        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            Determinism.WaitForAsync(() => false, timeout: TimeSpan.FromMilliseconds(80), description: "never happens"));
        Assert.Contains("never happens", exception.Message);
    }

    [Fact]
    public async Task WaitForAsync_supports_async_predicates()
    {
        var flag = false;
        await Determinism.WaitForAsync(
            () => Task.Run(() => flag = true),
            timeout: TimeSpan.FromSeconds(5),
            description: "async predicate flips flag");
        Assert.True(flag);
    }

    [Fact]
    public async Task CancelAfter_fires_after_timeout()
    {
        using var cts = Determinism.CancelAfter(TimeSpan.FromMilliseconds(50));
        Assert.False(cts.Token.IsCancellationRequested);
        await Task.Delay(300, CancellationToken.None);
        Assert.True(cts.Token.IsCancellationRequested);
    }

    [Fact]
    public void TempDirectory_creates_and_disposes()
    {
        string path;
        using (var temp = new TempDirectory("lifecycle"))
        {
            path = temp.FullPath;
            Assert.True(Directory.Exists(path));
            temp.WriteFile("nested/file.txt", [1, 2, 3]);
            Assert.True(File.Exists(Path.Combine(path, "nested", "file.txt")));
        }
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void CopyFixture_places_a_writable_copy()
    {
        using var temp = new TempDirectory("fixture-copy");
        var copied = TestAssets.CopyFixtureToTemp("odd_rgb.png", temp);
        Assert.True(File.Exists(copied));
        Assert.Equal(temp.FullPath, Path.GetDirectoryName(copied));
        File.WriteAllText(copied, "overwrite ok"); // copies are private, not shared TestData
    }
}
