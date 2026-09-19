using OrderSystem.Application.Common.Validation;

namespace OrderSystem.UnitTests.Common;

public sealed class ValidationResultTests
{
    [Fact]
    public void Success_ExposesValueWithoutErrors()
    {
        var result = ValidationResult.Success("normalized");

        Assert.True(result.IsValid);
        Assert.Equal("normalized", result.Value);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Failure_ExposesErrorsWithoutValue()
    {
        IReadOnlyDictionary<string, string[]> errors =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["name"] = ["Name is required."]
            };

        var result = ValidationResult.Failure<string>(errors);

        Assert.False(result.IsValid);
        Assert.Null(result.Value);
        Assert.Same(errors, result.Errors);
    }
}
