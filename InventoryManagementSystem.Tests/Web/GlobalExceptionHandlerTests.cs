using System.Net;
using System.Text.Json;
using FluentAssertions;
using FluentValidation;
using FluentValidation.Results;
using InventoryManagementSystem.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace InventoryManagementSystem.Tests.Web;

public class GlobalExceptionHandlerTests
{
    private readonly GlobalExceptionHandler _sut;

    public GlobalExceptionHandlerTests()
    {
        _sut = new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance);
    }

    private static (HttpContext context, Mock<IHostEnvironment> envMock) CreateHttpContext(string path, bool isDevelopment = false)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = path;
        httpContext.Response.Body = new MemoryStream();

        var envMock = new Mock<IHostEnvironment>();
        envMock.Setup(e => e.EnvironmentName).Returns(isDevelopment ? "Development" : "Production");

        var services = new ServiceCollection();
        services.AddSingleton(envMock.Object);
        httpContext.RequestServices = services.BuildServiceProvider();

        return (httpContext, envMock);
    }

    private static async Task<ProblemDetails?> ReadProblemDetails(HttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        return await JsonSerializer.DeserializeAsync<ProblemDetails>(context.Response.Body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    [Fact]
    public async Task TryHandleAsync_ValidationException_ApiPath_Returns400WithProblemDetails()
    {
        var (context, _) = CreateHttpContext("/api/v1/items");
        var failures = new List<ValidationFailure> { new("ItemCode", "Required") };
        var exception = new ValidationException(failures);

        var handled = await _sut.TryHandleAsync(context, exception, CancellationToken.None);

        handled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task TryHandleAsync_ValidationException_MvcPath_ReturnsFalse()
    {
        var (context, _) = CreateHttpContext("/Items/Create");
        var failures = new List<ValidationFailure> { new("ItemCode", "Required") };
        var exception = new ValidationException(failures);

        var handled = await _sut.TryHandleAsync(context, exception, CancellationToken.None);

        handled.Should().BeFalse();
    }

    [Fact]
    public async Task TryHandleAsync_ArgumentException_ApiPath_Returns400()
    {
        var (context, _) = CreateHttpContext("/api/v1/stock");
        var exception = new ArgumentException("Bad argument");

        var handled = await _sut.TryHandleAsync(context, exception, CancellationToken.None);

        handled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task TryHandleAsync_InvalidOperationException_ApiPath_Returns409()
    {
        var (context, _) = CreateHttpContext("/api/v1/stock/receive");
        var exception = new InvalidOperationException("Insufficient stock");

        var handled = await _sut.TryHandleAsync(context, exception, CancellationToken.None);

        handled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(409);
    }

    [Fact]
    public async Task TryHandleAsync_UnauthorizedAccessException_ApiPath_Returns403()
    {
        var (context, _) = CreateHttpContext("/api/v1/items");
        var exception = new UnauthorizedAccessException("No access");

        var handled = await _sut.TryHandleAsync(context, exception, CancellationToken.None);

        handled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task TryHandleAsync_KeyNotFoundException_ApiPath_Returns404()
    {
        var (context, _) = CreateHttpContext("/api/v1/items/999");
        var exception = new KeyNotFoundException("Not found");

        var handled = await _sut.TryHandleAsync(context, exception, CancellationToken.None);

        handled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task TryHandleAsync_UnknownException_ApiPath_Returns500()
    {
        var (context, _) = CreateHttpContext("/api/v1/items");
        var exception = new Exception("Something went wrong");

        var handled = await _sut.TryHandleAsync(context, exception, CancellationToken.None);

        handled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(500);
    }

    [Fact]
    public async Task TryHandleAsync_UnknownException_MvcPath_ReturnsFalse()
    {
        var (context, _) = CreateHttpContext("/Home/Index");
        var exception = new Exception("Something went wrong");

        var handled = await _sut.TryHandleAsync(context, exception, CancellationToken.None);

        handled.Should().BeFalse();
    }

    [Fact]
    public async Task TryHandleAsync_DevelopmentEnv_IncludesExceptionMessage()
    {
        var (context, _) = CreateHttpContext("/api/v1/items", isDevelopment: true);
        var exception = new InvalidOperationException("Detailed error message");

        await _sut.TryHandleAsync(context, exception, CancellationToken.None);

        var problem = await ReadProblemDetails(context);
        problem.Should().NotBeNull();
        problem!.Detail.Should().Contain("Detailed error message");
    }

    [Fact]
    public async Task TryHandleAsync_ProductionEnv_HidesExceptionDetail()
    {
        var (context, _) = CreateHttpContext("/api/v1/items", isDevelopment: false);
        var exception = new InvalidOperationException("Secret error details");

        await _sut.TryHandleAsync(context, exception, CancellationToken.None);

        var problem = await ReadProblemDetails(context);
        problem.Should().NotBeNull();
        problem!.Detail.Should().NotContain("Secret error details");
    }
}
