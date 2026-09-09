using ImageTagger.Tests.Preprocessing;
using ImageTagger.Tests.TestInfrastructure;
using Xunit;

namespace ImageTagger.Tests.QualityGates;

/// <summary>
/// Gate: committed C# sources must not contain machine-specific absolute paths.
/// This file intentionally contains synthetic samples and is excluded from the
/// repo-wide scan (see AbsolutePathScanner.ExcludedFiles).
/// </summary>
public class AbsolutePathGateTests
{
    private const string UserNameSample = "Nya";
    private static string UserName => Environment.UserName;

    [Fact]
    public void Rejects_windows_drive_letter_paths()
    {
        Assert.Equal("Windows drive-letter path", AbsolutePathScanner.CheckLine(@"var p = ""Q:\media\img_0001.png"";", UserNameSample));
        Assert.Equal("Windows drive-letter path", AbsolutePathScanner.CheckLine(@"// hint: C:/Users/devuser", UserNameSample));
    }

    [Fact]
    public void Rejects_home_directory_paths()
    {
        Assert.Equal("home-directory path", AbsolutePathScanner.CheckLine(@"/Users/devuser/pictures/a.png", UserNameSample));
        Assert.Equal("home-directory path", AbsolutePathScanner.CheckLine(@"\\Users\devuser\a.png", UserNameSample));
        Assert.Equal("home-directory path", AbsolutePathScanner.CheckLine(@"/home/devuser/.config/app.json", UserNameSample));
    }

    [Fact]
    public void Rejects_local_username_leak()
    {
        // a path-shaped leak is caught by the home rule first, which is fine:
        // both rules report the same file/line
        var line = "/Users/" + UserName + "/a.png";
        var reason = AbsolutePathScanner.CheckLine(line, UserName);
        Assert.True(reason is "local username leak" or "home-directory path", $"got: {reason}");
        // standalone occurrence, even without a path prefix
        Assert.Equal("local username leak", AbsolutePathScanner.CheckLine("TODO ping " + UserName + " about this", UserName));
    }

    [Fact]
    public void Accepts_urls_relative_paths_and_labels()
    {
        Assert.Null(AbsolutePathScanner.CheckLine("https://git-lfs.github.com/spec/v1", UserNameSample));
        Assert.Null(AbsolutePathScanner.CheckLine("Assets/Models/pack/model.onnx", UserNameSample));
        Assert.Null(AbsolutePathScanner.CheckLine(@"Path.Combine(root, ""sub"", ""x.png"")", UserNameSample));
        Assert.Null(AbsolutePathScanner.CheckLine("9:30 label ratio", UserNameSample));
    }

    [Fact]
    public void Scanner_source_and_infrastructure_are_themselves_clean()
    {
        // Self-check: the scanner and this file name must stay in sync so the
        // samples above can never leak into a real gate failure.
        Assert.Contains(
            "tests/ImageTagger.Tests/QualityGates/AbsolutePathGateTests.cs",
            AbsolutePathScanner.ExcludedFiles);
    }

    [Fact]
    public void Repo_sources_contain_no_machine_specific_paths()
    {
        var violations = AbsolutePathScanner.ScanSourceFiles(PreprocessingGoldenTests.RepoRoot, UserName);
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations.Select(v =>
            $"{v.Reason} at {v.RelativePath}:{v.Line}: {v.Text}")));
    }
}
