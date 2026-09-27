using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace OrderSystem.Api.OpenApi;

public sealed class IdempotencyKeyOperationFilter : IOperationFilter
{
    private const string ReplayHeaderName = "Idempotency-Replayed";

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);

        var isCreateOrder = string.Equals(
                context.ApiDescription.HttpMethod,
                HttpMethods.Post,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                context.ApiDescription.RelativePath?.Trim('/'),
                "api/orders",
                StringComparison.OrdinalIgnoreCase);

        if (!isCreateOrder)
        {
            return;
        }

        operation.Parameters ??= [];

        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "Idempotency-Key",
            In = ParameterLocation.Header,
            Required = true,
            Description = "Required UUID identifying one immutable CreateOrder intent for the authenticated user. " +
                "Repeating the same semantic request replays the original response; reusing the key for a " +
                "different request returns HTTP 409.",
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Format = "uuid"
            }
        });

        if (operation.Responses?.TryGetValue("201", out var createdResponse) is true &&
            createdResponse is OpenApiResponse concreteCreatedResponse)
        {
            concreteCreatedResponse.Headers ??= new Dictionary<string, IOpenApiHeader>();
            concreteCreatedResponse.Headers.TryAdd(ReplayHeaderName, new OpenApiHeader
            {
                Description = "True only when this response replays a previously completed request.",
                Schema = new OpenApiSchema
                {
                    Type = JsonSchemaType.Boolean
                }
            });
        }

        if (operation.Responses?.TryGetValue("409", out var conflictResponse) is true)
        {
            conflictResponse.Description =
                "Idempotency conflict. Stable error codes: IDEMPOTENCY_KEY_REUSED, " +
                "IDEMPOTENCY_KEY_EXPIRED, IDEMPOTENCY_REQUEST_PROCESSING.";
        }
    }
}
