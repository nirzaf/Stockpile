using Merconiq.Web.Controllers.Api.V1;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Merconiq.Web.Configuration;

internal sealed class StockReceiveIdempotencyKeyOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.ApiDescription.ActionDescriptor is not ControllerActionDescriptor action ||
            action.ControllerTypeInfo.AsType() != typeof(StockController) ||
            action.ActionName != nameof(StockController.Receive))
        {
            return;
        }

        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "Idempotency-Key",
            In = ParameterLocation.Header,
            Required = true,
            Schema = new OpenApiSchema { Type = JsonSchemaType.String, MaxLength = 200 }
        });
    }
}
