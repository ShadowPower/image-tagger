using ImageTagger.Tests.TestInfrastructure;
using Xunit;

namespace ImageTagger.Tests.QualityGates;

/// <summary>
/// Platform tests run only on the matching real OS; on any other machine they
/// skip with an explicit message so missing real-machine capability is never
/// mistaken for a code error.
/// </summary>
[Trait("Category", TestCategories.WindowsRuntime)]
public class WindowsRuntimeGateTests
{
    [Fact]
    public void Runs_only_on_real_windows()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("Requires the real Windows runtime (Win32/WindowsAppSDK ML). " +
                        "Run on a Windows machine or via the release pipeline.");
        Assert.True(OperatingSystem.IsWindows());
    }
}

[Trait("Category", TestCategories.MacRuntime)]
public class MacRuntimeGateTests
{
    [Fact]
    public void Runs_only_on_real_macos()
    {
        if (!OperatingSystem.IsMacOS())
            Assert.Skip("Requires the real macOS runtime (CoreML/Avalonia.Native). " +
                        "Run on a macOS machine or via the release pipeline.");
        Assert.True(OperatingSystem.IsMacOS());
    }
}
