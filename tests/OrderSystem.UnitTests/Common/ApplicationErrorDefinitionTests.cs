using System.Reflection;
using OrderSystem.Application.Common.Results;

namespace OrderSystem.UnitTests.Common;

public sealed class ApplicationErrorDefinitionTests
{
    [Fact]
    public void Create_WithoutOverride_UsesDefinitionDefaults()
    {
        var definition = new ApplicationErrorDefinition(
            ApplicationErrorKind.NotFound,
            "RESOURCE_NOT_FOUND",
            "The resource was not found.");

        var error = definition.Create();

        Assert.Equal(ApplicationErrorKind.NotFound, error.Kind);
        Assert.Equal("RESOURCE_NOT_FOUND", error.Code);
        Assert.Equal("The resource was not found.", error.Message);
        Assert.Null(error.ValidationErrors);
    }

    [Fact]
    public void Create_WithMessageOverride_PreservesKindAndCode()
    {
        var error = ApplicationErrors.Forbidden.Create(
            message: "Only Customers can create Orders.");

        Assert.Equal(ApplicationErrorKind.Forbidden, error.Kind);
        Assert.Equal("FORBIDDEN", error.Code);
        Assert.Equal("Only Customers can create Orders.", error.Message);
    }

    [Fact]
    public void Create_ValidationDefinition_PreservesValidationErrors()
    {
        IReadOnlyDictionary<string, string[]> errors =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["items"] = ["At least one item is required."]
            };

        var error = ApplicationErrors.ValidationFailed.Create(
            validationErrors: errors);

        Assert.Equal(ApplicationErrorKind.Validation, error.Kind);
        Assert.Equal("VALIDATION_FAILED", error.Code);
        Assert.Same(errors, error.ValidationErrors);
    }

    [Fact]
    public void Create_NonValidationDefinitionWithValidationErrors_Throws()
    {
        IReadOnlyDictionary<string, string[]> errors =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["id"] = ["ID is required."]
            };

        var action = () => ApplicationErrors.Unauthorized.Create(
            validationErrors: errors);

        Assert.Throws<InvalidOperationException>(action);
    }

    [Fact]
    public void Create_ValidationDefinitionWithoutValidationErrors_UsesDefaultMessage()
    {
        var error = ApplicationErrors.ValidationFailed.Create();

        Assert.Equal(ApplicationErrorKind.Validation, error.Kind);
        Assert.Equal("VALIDATION_FAILED", error.Code);
        Assert.Equal("One or more validation errors occurred.", error.Message);
        Assert.Null(error.ValidationErrors);
    }

    [Theory]
    [InlineData("")]
    [InlineData("validation_failed")]
    [InlineData("VALIDATION-FAILED")]
    [InlineData(" VALIDATION_FAILED")]
    public void Constructor_WithInvalidCode_Throws(string code)
    {
        var action = () => new ApplicationErrorDefinition(
            ApplicationErrorKind.Validation,
            code,
            "Validation failed.");

        Assert.Throws<ArgumentException>(action);
    }

    [Fact]
    public void Create_WithBlankMessageOverride_Throws()
    {
        var action = () => ApplicationErrors.Forbidden.Create(message: " ");

        Assert.Throws<ArgumentException>(action);
    }

    [Fact]
    public void ApplicationErrors_AllDefinitions_HaveUniqueCodes()
    {
        var duplicateCodes = GetDefinitions(typeof(ApplicationErrors))
            .GroupBy(definition => definition.Code, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        Assert.Empty(duplicateCodes);
    }

    private static IEnumerable<ApplicationErrorDefinition> GetDefinitions(Type catalogType)
    {
        foreach (var field in catalogType.GetFields(
                     BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (field.FieldType == typeof(ApplicationErrorDefinition) &&
                field.GetValue(null) is ApplicationErrorDefinition definition)
            {
                yield return definition;
            }
        }

        foreach (var nestedType in catalogType.GetNestedTypes(BindingFlags.Public))
        {
            foreach (var definition in GetDefinitions(nestedType))
            {
                yield return definition;
            }
        }
    }
}
