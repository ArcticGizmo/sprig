using Sprig.Core.Store;

namespace Sprig.Tests.Store;

/// <summary>SPRIG_STORE is process-wide, so this shares the non-parallel collection with the console tests
/// rather than racing other tests that build a default <see cref="SprigPaths"/>.</summary>
[Collection("cli-console")]
public class SprigPathsTests
{
    [Fact]
    public void The_default_root_follows_SPRIG_STORE_and_an_explicit_root_wins()
    {
        var previous = Environment.GetEnvironmentVariable("SPRIG_STORE");
        try
        {
            Environment.SetEnvironmentVariable("SPRIG_STORE", @"C:\sandbox-store");
            Assert.Equal(@"C:\sandbox-store", new SprigPaths().Root);
            Assert.Equal(@"C:\explicit", new SprigPaths(@"C:\explicit").Root);

            Environment.SetEnvironmentVariable("SPRIG_STORE", "  ");
            Assert.EndsWith(AppProfile.DataFolderName, new SprigPaths().Root);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SPRIG_STORE", previous);
        }
    }
}
