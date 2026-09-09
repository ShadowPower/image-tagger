using Avalonia;
using Avalonia.Themes.Simple;

namespace ImageTagger.Tests.TestInfrastructure;

/// <summary>
/// Minimal headless application for Avalonia UI tests. Deliberately independent
/// of the real App class so test infrastructure compiles while UI workflows are
/// still being developed in parallel; U-01 swaps in the real App if needed.
/// </summary>
public sealed class HeadlessTestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new SimpleTheme());
    }
}
