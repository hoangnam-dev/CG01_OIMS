using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OrderSystem.Application.Orders.Contracts;

namespace OrderSystem.Application.Orders;

/// <summary>
/// Computes the frozen create-order:v1 request fingerprint defined by ADR 0003.
/// Only product variant IDs and quantities participate in the canonical input.
/// </summary>
public static class CreateOrderRequestHasher
{
    private const string Prefix = "create-order:v1\n";

    public static byte[] Hash(IReadOnlyList<CreateOrderItemRequest> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            throw new ArgumentException("At least one order item is required.", nameof(items));
        }
        if (items.Any(item => item.ProductVariantId == Guid.Empty))
        {
            throw new ArgumentException("Product variant ID is required.", nameof(items));
        }
        if (items.Any(item => item.Quantity <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(items), "Quantity must be greater than zero.");
        }
        bool duplicateVariant = items
            .Select(item => item.ProductVariantId)
            .Distinct()
            .Count() != items.Count;
        if (duplicateVariant)
        {
            throw new ArgumentException("Each product variant may appear only once.", nameof(items));
        }

        var canonicalItems = items.Select(item => new
        {
            VariantId = item.ProductVariantId.ToString("D").ToLowerInvariant(),
            item.Quantity
        })
            .OrderBy(item => item.VariantId, StringComparer.Ordinal);

        var canonical = new StringBuilder(Prefix);

        foreach (var item in canonicalItems)
        {
            canonical.Append(item.VariantId);
            canonical.Append(':');
            canonical.Append(item.Quantity.ToString(CultureInfo.InvariantCulture));
            canonical.Append('\n');
        }

        return SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
    }
}