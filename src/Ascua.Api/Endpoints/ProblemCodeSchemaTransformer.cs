using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Ascua.Api.Endpoints;

/// <summary>Documenta en el esquema de Problem Details la extensión <c>code</c> que agrega <see cref="Problems"/>.</summary>
internal sealed class ProblemCodeSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        if (context.JsonTypeInfo.Type.IsAssignableTo(typeof(ProblemDetails)))
        {
            schema.Properties ??= new Dictionary<string, IOpenApiSchema>();
            schema.Properties["code"] = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Description = "Código estable del error (p. ej. validation, hero-taken); es el que deben usar los clientes.",
            };
        }

        return Task.CompletedTask;
    }
}
