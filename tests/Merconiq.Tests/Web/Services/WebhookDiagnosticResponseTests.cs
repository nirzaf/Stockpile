using System.Text;
using FluentAssertions;
using Merconiq.Web.BackgroundServices;

namespace Merconiq.Tests.Web.Services;

public sealed class WebhookDiagnosticResponseTests
{
    [Fact]
    public async Task ReadDiagnosticResponseAsync_BoundsResponseAndRedactsConfiguredSecrets()
    {
        const string secret = "shared-secret";
        const string url = "https://hooks.example.test/events?token=endpoint-token";
        var responseText = string.Concat(Enumerable.Repeat($"{secret} {url} ", 500));
        using var content = new StringContent(responseText, Encoding.UTF8, "text/plain");

        var diagnostic = await WebhookDeliveryBackgroundService.ReadDiagnosticResponseAsync(
            content,
            secret,
            url,
            CancellationToken.None);

        diagnostic.Should().Contain("[truncated]");
        diagnostic.Should().NotContain(secret);
        diagnostic.Should().NotContain(url);
        Encoding.UTF8.GetByteCount(diagnostic).Should().BeLessThanOrEqualTo(
            WebhookDeliveryBackgroundService.MaximumDiagnosticResponseBytes);
    }

    [Fact]
    public async Task ReadDiagnosticResponseAsync_PreservesShortUtf8ResponseWhileRedactingSecrets()
    {
        const string secret = "token-123";
        const string url = "https://hooks.example.test/path";
        using var content = new StringContent($"accepted {secret} {url} café", Encoding.UTF8, "text/plain");

        var diagnostic = await WebhookDeliveryBackgroundService.ReadDiagnosticResponseAsync(
            content,
            secret,
            url,
            CancellationToken.None);

        diagnostic.Should().Be($"accepted {new string('*', secret.Length)} [url] café");
    }

    [Fact]
    public async Task ReadDiagnosticResponseAsync_DoesNotReturnPartialUtf8CharacterAtLimit()
    {
        var responseText = new string('é', WebhookDeliveryBackgroundService.MaximumDiagnosticResponseBytes);
        using var content = new StringContent(responseText, Encoding.UTF8, "text/plain");

        var diagnostic = await WebhookDeliveryBackgroundService.ReadDiagnosticResponseAsync(
            content,
            secret: null,
            url: string.Empty,
            CancellationToken.None);

        diagnostic.Should().EndWith("[truncated]");
        Encoding.UTF8.GetByteCount(diagnostic).Should().BeLessThanOrEqualTo(
            WebhookDeliveryBackgroundService.MaximumDiagnosticResponseBytes);
        diagnostic.Should().NotContain("\uFFFD");
    }

    [Fact]
    public async Task ReadDiagnosticResponseAsync_DropsPartialUrlAtTruncationBoundary()
    {
        const string url = "https://hooks.example.test/private?token=long-secret-value";
        var prefixLength = WebhookDeliveryBackgroundService.MaximumDiagnosticResponseBytes - url.Length / 2;
        using var content = new StringContent(
            new string('x', prefixLength) + url + new string('y', 100),
            Encoding.UTF8,
            "text/plain");

        var diagnostic = await WebhookDeliveryBackgroundService.ReadDiagnosticResponseAsync(
            content,
            secret: null,
            url,
            CancellationToken.None);

        diagnostic.Should().NotContain("https://hooks");
        diagnostic.Should().NotContain("token=long-secret-value");
        diagnostic.Should().EndWith("[truncated]");
    }
}
