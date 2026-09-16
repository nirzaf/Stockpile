using FluentAssertions;
using Merconiq.Web.Security;

namespace Merconiq.Tests.Web.Services;

public class WebhookUrlValidatorTests
{
    [Theory]
    [InlineData("https://localhost")]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://10.0.0.4")]
    [InlineData("https://192.168.1.20")]
    [InlineData("https://169.254.169.254")]
    [InlineData("https://[::1]")]
    [InlineData("https://[fc00::1]")]
    public async Task Rejects_private_loopback_link_local_and_metadata_targets(string url)
    {
        (await WebhookUrlValidator.ValidateAsync(url)).Should().NotBeNull();
    }

    [Fact]
    public async Task Rejects_non_default_https_ports_and_user_information()
    {
        (await WebhookUrlValidator.ValidateAsync("https://user@example.com")).Should().NotBeNull();
        (await WebhookUrlValidator.ValidateAsync("https://example.com:8443")).Should().NotBeNull();
    }
}
