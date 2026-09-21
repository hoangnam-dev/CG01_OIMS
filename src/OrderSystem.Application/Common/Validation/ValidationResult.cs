namespace OrderSystem.Application.Common.Validation;

public sealed class ValidationResult<T>
{
    internal ValidationResult(T? value, IReadOnlyDictionary<string, string[]> errors)
    {
        Value = value;
        Errors = errors;
    }

    public bool IsValid => Errors.Count == 0;

    public T? Value { get; }

    public IReadOnlyDictionary<string, string[]> Errors { get; }
}

public static class ValidationResult
{
    public static ValidationResult<T> Success<T>(T value) =>
        new(value, new Dictionary<string, string[]>(StringComparer.Ordinal));

    public static ValidationResult<T> Failure<T>(IReadOnlyDictionary<string, string[]> errors) =>
        new(default, errors);
}
