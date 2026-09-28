using System.Net;
using Goulash.Application;
using Xunit;

namespace Goulash.Api.Tests;

public sealed class SupplierDiscoverySourcePolicyTests
{
    [Theory]
    [InlineData("https://supplier.com/", true)]
    [InlineData("https://localhost/", false)]
    [InlineData("http://127.0.0.1/", false)]
    [InlineData("file:///etc/passwd", false)]
    public void WebsiteUrlsAreRestrictedOnlyForSafeNetworkRetrieval(string value, bool expected)
    {
        Assert.Equal(expected, SupplierDiscoverySourcePolicy.TryNormalizeHttpUrl(value, out _));
    }

    [Fact]
    public void PublicAddressCheckRejectsLoopback()
    {
        Assert.False(SupplierDiscoverySourcePolicy.IsPublicAddress(IPAddress.Loopback));
        Assert.True(SupplierDiscoverySourcePolicy.IsPublicAddress(IPAddress.Parse("8.8.8.8")));
    }
}
