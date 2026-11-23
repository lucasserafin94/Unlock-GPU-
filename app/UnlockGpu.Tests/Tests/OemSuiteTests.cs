namespace UnlockGpu.Tests;

/// <summary>Runs the self-contained <see cref="OemTests"/> checks through the reflection test runner.</summary>
public sealed class OemSuiteTests
{
    [Test]
    public void OemChecks_AllPass()
    {
        var failures = OemTests.Run();
        if (failures.Count > 0)
            throw new AssertionException(failures.Count + " OEM check(s) failed:\n    " + string.Join("\n    ", failures));
    }
}
