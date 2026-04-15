using Agw.Files.Controllers;

namespace Agw.Files.Tests;

public class FilesModuleOwnershipTests
{
    [Fact]
    public void FilesController_LivesInAgwFilesAssembly()
    {
        Assert.Equal("Agw.Files", typeof(FilesController).Assembly.GetName().Name);
    }
}
