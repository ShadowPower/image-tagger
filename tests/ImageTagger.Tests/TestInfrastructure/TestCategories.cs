namespace ImageTagger.Tests.TestInfrastructure;

/// <summary>
/// Canonical test categories applied with <c>[Trait("Category", ...)]</c>.
/// Filter examples (vstest):
///   - default commit gate:      --filter "Category!=RealModel&amp;Category!=WindowsRuntime&amp;Category!=MacRuntime&amp;Category!=Visual"
///   - release pipeline (model): --filter "Category=RealModel"
///   - real Windows runtime:     --filter "Category=WindowsRuntime"
///   - real macOS runtime:       --filter "Category=MacRuntime"
///   - visual/headless rendering:--filter "Category=Visual"
/// Failure semantics: a red test with no trait is a code error; a skipped
/// WindowsRuntime/MacRuntime test reports missing real-machine capability;
/// a skipped RealModel test reports a missing or un-pulled LFS asset.
/// </summary>
public static class TestCategories
{
    public const string Unit = "Unit";

    public const string Integration = "Integration";

    public const string RealModel = "RealModel";

    public const string WindowsRuntime = "WindowsRuntime";

    public const string MacRuntime = "MacRuntime";

    public const string Visual = "Visual";
}
