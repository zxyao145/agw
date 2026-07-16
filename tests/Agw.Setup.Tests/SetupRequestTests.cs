using System.ComponentModel.DataAnnotations;

using Agw.Setup.Contracts;
using Agw.Shared.Configuration;

using Xunit;

namespace Agw.Setup.Tests;

public class SetupRequestTests
{
    [Theory]
    [InlineData("1234567", false)]
    [InlineData("12345678", true)]
    public void Validate_AdminPasswordLengthBoundary_ReturnsExpectedResult(string password, bool expected)
    {
        var request = new SetupRequest
        {
            Provider = DatabaseProvider.Sqlite,
            ConnectionString = "Data Source=agw.db",
            AdminPassword = password
        };
        var validationContext = new ValidationContext(request);

        var isValid = Validator.TryValidateObject(request, validationContext, [], validateAllProperties: true);

        Assert.Equal(expected, isValid);
    }
}
