using System.Text.Json;
using System.Text.Json.Serialization;
using OrderSystem.Application.Products.Contracts;

namespace OrderSystem.Infrastructure.Products.Caching;

internal static class ProductDetailCacheSerializer
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };

    public static byte[] Serialize(ProductDetailDto product)
    {
        ArgumentNullException.ThrowIfNull(product);

        return JsonSerializer.SerializeToUtf8Bytes(product, SerializerOptions);
    }

    public static bool TryDeserialize(ReadOnlySpan<byte> payload, out ProductDetailDto? product)
    {
        try
        {
            var deserialized  = JsonSerializer.Deserialize<ProductDetailDto>(payload, SerializerOptions);
            if (deserialized is null || deserialized.Variants is null)
            {
                product = null;
                return false;
            }
            product = deserialized;
            return true;
        }
        catch (JsonException)
        {
            product = null;
            return false;
        }
    }
}