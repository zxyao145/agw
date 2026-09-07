using System.ComponentModel.DataAnnotations;
using Agw.Setup.Contracts;
using Xunit;

namespace Agw.Setup.Tests;

public class SetupRequestTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(7, false)]
    [InlineData(8, true)]
    [InlineData(256, true)]
    [InlineData(257, false)]
    public void Validate_AdminPasswordLengthBoundary_ReturnsExpectedResult(int length, bool expected)
    {
        var request = new SetupRequest { AdminPassword = new string('a', length) };
        var results = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(
            request,
            new ValidationContext(request),
            results,
            validateAllProperties: true
        );

        Assert.Equal(expected, isValid);
        if (!expected)
            Assert.Contains(results, result => result.MemberNames.Contains(nameof(request.AdminPassword)));
    }
}
