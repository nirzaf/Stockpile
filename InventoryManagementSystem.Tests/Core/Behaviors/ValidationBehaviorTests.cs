using FluentAssertions;
using FluentValidation;
using FluentValidation.Results;
using InventoryManagementSystem.Core.Behaviors;
using MediatR;

namespace InventoryManagementSystem.Tests.Core.Behaviors;

public class ValidationBehaviorTests
{
    [Fact]
    public async Task Handle_NoValidators_CallsNext()
    {
        var validators = Enumerable.Empty<IValidator<TestRequest>>();
        var sut = new ValidationBehavior<TestRequest, string>(validators);
        var nextCalled = false;
        RequestHandlerDelegate<string> next = (ct) => { nextCalled = true; return Task.FromResult("ok"); };

        var result = await sut.Handle(new TestRequest("hello"), next, CancellationToken.None);

        nextCalled.Should().BeTrue();
        result.Should().Be("ok");
    }

    [Fact]
    public async Task Handle_ValidRequest_CallsNext()
    {
        var validator = new TestRequestValidator(passValidation: true);
        var sut = new ValidationBehavior<TestRequest, string>(new IValidator<TestRequest>[] { validator });
        var nextCalled = false;
        RequestHandlerDelegate<string> next = (ct) => { nextCalled = true; return Task.FromResult("ok"); };

        var result = await sut.Handle(new TestRequest("valid"), next, CancellationToken.None);

        nextCalled.Should().BeTrue();
        result.Should().Be("ok");
    }

    [Fact]
    public async Task Handle_InvalidRequest_ThrowsValidationException()
    {
        var validator = new TestRequestValidator(passValidation: false);
        var sut = new ValidationBehavior<TestRequest, string>(new IValidator<TestRequest>[] { validator });
        RequestHandlerDelegate<string> next = (ct) => Task.FromResult("should not reach");

        var act = () => sut.Handle(new TestRequest(""), next, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>()
            .Where(e => e.Errors.Any(f => f.PropertyName == "Name"));
    }

    [Fact]
    public async Task Handle_MultipleValidators_AggregatesFailures()
    {
        var v1 = new TestRequestValidator(passValidation: false, failureProperty: "Name");
        var v2 = new TestRequestValidator(passValidation: false, failureProperty: "Value");

        var sut = new ValidationBehavior<TestRequest, string>(new IValidator<TestRequest>[] { v1, v2 });
        RequestHandlerDelegate<string> next = (ct) => Task.FromResult("should not reach");

        var act = () => sut.Handle(new TestRequest(""), next, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.Which.Errors.Should().HaveCountGreaterThanOrEqualTo(2);
    }

    private record TestRequest(string Name) : IRequest<string>;

    private class TestRequestValidator : AbstractValidator<TestRequest>
    {
        public TestRequestValidator(bool passValidation, string failureProperty = "Name")
        {
            if (!passValidation)
            {
                RuleFor(x => x.Name)
                    .Must(_ => false)
                    .WithMessage($"{failureProperty} is invalid")
                    .OverridePropertyName(failureProperty);
            }
        }
    }
}
