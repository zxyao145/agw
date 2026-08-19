using Agw.Shared.Data.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Tests;

public class EFContextTests
{
    [Fact]
    public async Task TransactionCommitWithoutActiveTransaction_ReturnsFalse()
    {
        await using var context = new EFContext(new DbContextOptions<EFContext>());

        Assert.False(await context.TransactionCommitAsync(TestContext.Current.CancellationToken));
    }
}
