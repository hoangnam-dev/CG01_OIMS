using System.Net.Mail;

namespace OrderSystem.Api.Authentication;

public sealed class AdminBootstrapOptions
{
    public const string SectionName = "AdminBootstrap";

    public bool Enabled { get; init; }

    public string? Email { get; init; }

    public string? Password { get; init; }

    public bool HasValidEmail()
    {
        var value = Email?.Trim();
        return !string.IsNullOrEmpty(value) &&
               MailAddress.TryCreate(value, out var parsed) &&
               string.Equals(parsed.Address, value, StringComparison.OrdinalIgnoreCase);
    }

    public bool HasValidPasswordLength()
    {
        var length = Password?.EnumerateRunes().Count() ?? 0;
        return length is >= 8 and <= 13;
    }
}
