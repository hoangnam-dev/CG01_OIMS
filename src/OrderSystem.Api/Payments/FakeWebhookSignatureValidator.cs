using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace OrderSystem.Api.Payments;

public sealed class FakeWebhookSignatureValidator
{
    private readonly byte[] _secret;
    private readonly TimeSpan _allowedClockSkew;

    public FakeWebhookSignatureValidator(byte[] secret, TimeSpan allowedClockSkew)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.Length < 32)
        {
            throw new ArgumentException("Secret must contain at least 32 bytes", nameof(secret));
        }

        _secret = secret.ToArray();
        _allowedClockSkew = allowedClockSkew;
    }

    public bool IsValid(string timestamp, string signature, ReadOnlySpan<byte> requestBody, DateTimeOffset now)
    {
        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var unixSeconds))
        {
            return false;
        }

        DateTimeOffset signedAt;
        try
        {
            signedAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        if ((now - signedAt).Duration() > _allowedClockSkew)
        {
            return false;
        }

        byte[] suppliedSignature;
        try
        {
            suppliedSignature = Convert.FromHexString(signature);
        }
        catch (FormatException)
        {
            return false;
        }

        var prefix = Encoding.UTF8.GetBytes($"{timestamp}.");
        var signedPayload = new byte[prefix.Length + requestBody.Length];
        prefix.CopyTo(signedPayload, 0);
        requestBody.CopyTo(signedPayload.AsSpan(prefix.Length));

        var expectedSignature = HMACSHA256.HashData(_secret, signedPayload);
        return suppliedSignature.Length == expectedSignature.Length &&
            CryptographicOperations.FixedTimeEquals(suppliedSignature, expectedSignature);
    }
}