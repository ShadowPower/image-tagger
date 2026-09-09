using ImageTagger.Tests.Preprocessing;
using ImageTagger.Tests.TestData;

namespace ImageTagger.Tests.TestInfrastructure;

/// <summary>
/// Copies committed test fixtures into a scratch directory so integration tests
/// never mutate the shared TestData folder.
/// </summary>
public static class TestAssets
{
    public static string CopyFixture(string fixtureName, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        var source = Path.Combine(PreprocessingGoldenTests.FixturesDir, fixtureName);
        var destination = Path.Combine(destinationDirectory, fixtureName);
        File.Copy(source, destination, overwrite: true);
        return destination;
    }

    public static string CopyFixtureToTemp(string fixtureName, TempDirectory temp)
        => CopyFixture(fixtureName, temp.FullPath);
}
