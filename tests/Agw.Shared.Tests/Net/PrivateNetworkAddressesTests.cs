using System.Net;
using Agw.Shared.Net;

namespace Agw.Shared.Tests.Net;

public sealed class PrivateNetworkAddressesTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.1.1", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("224.0.0.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("::ffff:10.0.0.1", true)]
    [InlineData("2001:4860:4860::8888", false)]
    public void IsPrivate_KnownRanges_MatchesExpected(string address, bool expected) =>
        Assert.Equal(expected, PrivateNetworkAddresses.IsPrivate(IPAddress.Parse(address)));
}
