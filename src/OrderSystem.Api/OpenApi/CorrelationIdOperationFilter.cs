using Microsoft.OpenApi;
using OrderSystem.Api.Diagnostics;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace OrderSystem.Api.OpenApi;

public sealed class CorrelationIdOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);

        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = CorrelationIdMiddleware.HeaderName,
            In = ParameterLocation.Header,
            Required = false,
            Description = "Optional UUID correlation identifier. The API generates one when absent.",
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Format = "uuid"
            }
        });

        if (operation.Responses is null)
        {
            return;
        }

        foreach (var response in operation.Responses.Values)
        {
            response.Headers?.TryAdd(
                CorrelationIdMiddleware.HeaderName,
                new OpenApiHeader
                {
                    Description = "Resolved UUID correlation identifier returned for this request.",
                    Schema = new OpenApiSchema
                    {
                        Type = JsonSchemaType.String,
                        Format = "uuid"
                    }
                });
        }
    }
}
