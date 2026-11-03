using Sprig.Engine.Store;

namespace Sprig.Tests.Store;

public class AppProfileTests
{
    [Fact]
    public void The_experimental_store_never_shares_a_folder_with_the_released_app()
    {
        Assert.StartsWith("sprig-exp", AppProfile.DataFolderName);
        Assert.NotEqual("sprig", AppProfile.DataFolderName);
        Assert.NotEqual("sprig (Dev)", AppProfile.DataFolderName);
    }
}
