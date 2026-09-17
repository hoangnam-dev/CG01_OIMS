namespace OrderSystem.Application.Common.Results;

public enum ApplicationErrorKind
{
    Validation,
    NotFound,
    Conflict,
    Forbidden
}

public sealed record ApplicationError(
    ApplicationErrorKind Kind,
    string Code,
    string Message,
    IReadOnlyDictionary<string, string[]>? ValidationErrors = null);

public sealed class ApplicationResult<T>
{
    internal ApplicationResult(T? value, ApplicationError? error)
    {
        Value = value;
        Error = error;
    }

    public bool IsSuccess => Error is null;

    public T? Value { get; }

    public ApplicationError? Error { get; }
}

public static class ApplicationResult
{
    public static ApplicationResult<T> Success<T>(T value) => new(value, null);

    public static ApplicationResult<T> Failure<T>(ApplicationError error) => new(default, error);
}
