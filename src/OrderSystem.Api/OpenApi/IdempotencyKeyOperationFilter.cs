using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace OrderSystem.Api.OpenApi;

public sealed class IdempotencyKeyOperationFilter : IOperationFilter
{
  public void Apply(OpenApiOperation operation, OperationFilterContext context)
  {
    ArgumentNullException.ThrowIfNull(operation);
    ArgumentNullException.ThrowIfNull(context);

    var isCreateOrder = string.Equals(context.ApiDescription.HttpMethod, HttpMethods.Post, StringComparison.OrdinalIgnoreCase)
      && string.Equals(context.ApiDescription.RelativePath?.Trim('/'), "api/orders", StringComparison.OrdinalIgnoreCase);

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
      Description = "Required non-empty UUID. Sprint 4 validates syntax only; " +
                "persistent replay and duplicate suppression start in Sprint 5.",
      Schema = new OpenApiSchema
      {
        Type = JsonSchemaType.String,
        Format = "uuid"
      }
    });
  }
}