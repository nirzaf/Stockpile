using Merconiq.Web.Controllers.Api.V1;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Merconiq.Web.Configuration;

internal sealed class StockIdempotencyKeyOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.ApiDescription.ActionDescriptor is not ControllerActionDescriptor action ||
            action.ControllerTypeInfo.AsType() != typeof(StockController))
        {
            return;
        }

        var isRequired = action.ActionName == nameof(StockController.Receive);
        var supportsIdempotencyKey = isRequired ||
            action.ActionName == nameof(StockController.Transfer) ||
            action.ActionName == nameof(StockController.Sell) ||
            action.ActionName == nameof(StockController.Quarantine) ||
            action.ActionName == nameof(StockController.ReleaseQuarantine);
        if (!supportsIdempotencyKey)
        {
            return;
        }

        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "Idempotency-Key",
            In = ParameterLocation.Header,
            Required = isRequired,
            Schema = new OpenApiSchema { Type = JsonSchemaType.String, MaxLength = 200 }
        });
    }
}
