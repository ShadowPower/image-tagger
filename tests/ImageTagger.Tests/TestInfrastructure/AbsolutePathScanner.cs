using System.Text;
using System.Text.RegularExpressions;

namespace ImageTagger.Tests.TestInfrastructure;

/// <summary>One absolute-path leak found in a source file.</summary>
/// <param name="RelativePath">Repo-relative forward-slash path of the file.</param>
/// <param name="Line">1-based line number.</param>
/// <param name="Text">The offending line content.</param>
/// <param name="Reason">Human-readable rule name for the failure message.</param>
public sealed record PathViolation(string RelativePath, int Line, string Text, string Reason);

/// <summary>
/// Quality gate that rejects machine-specific paths in committed C# sources:
/// Windows drive-letter paths, POSIX/Windows home-directory paths and the local
/// username. Rules are regexes so this source file never contains a literal
/// sample; concrete samples live only in the gate tests.
/// </summary>
public static partial class AbsolutePathScanner
{
    /// <summary>Drive-letter followed by a separator; the lookbehind keeps "https://" out.</summary>
    [GeneratedRegex(@"(?<![A-Za-z])[A-Za-z]:[\\/]", RegexOptions.Compiled)]
    private static partial Regex DriveLetterPattern();

    /// <summary>Home directory path: slash-Users-name, slash-home-name, incl. backslash variants.</summary>
    [GeneratedRegex(@"[\\/]Users[\\/][A-Za-z0-9_.\-]+|[\\/]home[\\/][A-Za-z0-9_.\-]+", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex HomeDirectoryPattern();

    /// <summary>Files whose purpose is to test the scanner itself.</summary>
    public static readonly IReadOnlyList<string> ExcludedFiles =
    [
        "tests/ImageTagger.Tests/QualityGates/AbsolutePathGateTests.cs",
    ];

    /// <summary>Checks a single line; used directly by the gate tests with synthetic samples.</summary>
    public static string? CheckLine(string line, string userName)
    {
        if (DriveLetterPattern().IsMatch(line))
            return "Windows drive-letter path";
        if (HomeDirectoryPattern().IsMatch(line))
            return "home-directory path";
        if (userName.Length >= 3 && Regex.IsMatch(line, $@"\b{Regex.Escape(userName)}\b"))
            return "local username leak";
        return null;
    }

    /// <summary>Scans all .cs sources under src/ and tests/ in the given repo root.</summary>
    public static IReadOnlyList<PathViolation> ScanSourceFiles(string repoRoot, string userName)
    {
        var violations = new List<PathViolation>();
        foreach (var relative in EnumerateSources(repoRoot))
        {
            var lines = File.ReadAllLines(Path.Combine(repoRoot, relative));
            for (var i = 0; i < lines.Length; i++)
            {
                var reason = CheckLine(lines[i], userName);
                if (reason is not null)
                    violations.Add(new PathViolation(relative, i + 1, lines[i].Trim(), reason));
            }
        }
        return violations;
    }

    private static IEnumerable<string> EnumerateSources(string repoRoot)
    {
        foreach (var top in new[] { "src", "tests" })
        {
            var root = Path.Combine(repoRoot, top);
            if (!Directory.Exists(root))
                continue;
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
                if (relative.Contains("/bin/") || relative.Contains("/obj/"))
                    continue;
                if (ExcludedFiles.Contains(relative, StringComparer.OrdinalIgnoreCase))
                    continue;
                yield return relative;
            }
        }
    }
}
