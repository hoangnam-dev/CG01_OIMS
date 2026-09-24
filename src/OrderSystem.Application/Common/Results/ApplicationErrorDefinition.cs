namespace OrderSystem.Application.Common.Results;

public sealed record ApplicationErrorDefinition
{
    public ApplicationErrorDefinition(
        ApplicationErrorKind kind,
        string code,
        string defaultMessage)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Application error kind is invalid.");
        }

        if (!IsValidCode(code))
        {
            throw new ArgumentException(
                "Application error code must contain only uppercase ASCII letters, digits, and underscores.",
                nameof(code));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(defaultMessage);

        Kind = kind;
        Code = code;
        DefaultMessage = defaultMessage;
    }

    public ApplicationErrorKind Kind { get; }

    public string Code { get; }

    public string DefaultMessage { get; }

    public ApplicationError Create(
        IReadOnlyDictionary<string, string[]>? validationErrors = null,
        string? message = null)
    {
        if (Kind != ApplicationErrorKind.Validation && validationErrors is not null)
        {
            throw new InvalidOperationException(
                "Validation errors can be attached only to a Validation application error.");
        }

        if (message is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(message);
        }

        return new ApplicationError(
            Kind,
            Code,
            message ?? DefaultMessage,
            validationErrors);
    }

    private static bool IsValidCode(string? code) =>
        !string.IsNullOrWhiteSpace(code) &&
        code.All(character =>
            character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');
}
