namespace OrderSystem.Application.Common.Results;

public static class ApplicationErrors
{
    public static readonly ApplicationErrorDefinition ValidationFailed = new(
        ApplicationErrorKind.Validation,
        "VALIDATION_FAILED",
        "One or more validation errors occurred.");

    public static readonly ApplicationErrorDefinition Unauthorized = new(
        ApplicationErrorKind.Unauthorized,
        "UNAUTHORIZED",
        "Authentication is required.");

    public static readonly ApplicationErrorDefinition Forbidden = new(
        ApplicationErrorKind.Forbidden,
        "FORBIDDEN",
        "The current user is not authorized to perform this action.");

    public static class Authentication
    {
        public static readonly ApplicationErrorDefinition RegistrationConflict = new(
            ApplicationErrorKind.Conflict,
            "REGISTRATION_CONFLICT",
            "The registration identity is unavailable.");

        public static readonly ApplicationErrorDefinition InvalidRefreshToken = new(
            ApplicationErrorKind.Unauthorized,
            "INVALID_REFRESH_TOKEN",
            "The refresh token is invalid.");
    }

    public static class Products
    {
        public static readonly ApplicationErrorDefinition NotFound = new(
            ApplicationErrorKind.NotFound,
            "PRODUCT_NOT_FOUND",
            "The Product was not found.");

        public static readonly ApplicationErrorDefinition VariantNotFound = new(
            ApplicationErrorKind.NotFound,
            "PRODUCT_VARIANT_NOT_FOUND",
            "The Product Variant was not found.");

        public static readonly ApplicationErrorDefinition NotActive = new(
            ApplicationErrorKind.Conflict,
            "PRODUCT_NOT_ACTIVE",
            "Orders can contain only active Products.");

        public static readonly ApplicationErrorDefinition VariantNotActive = new(
            ApplicationErrorKind.Conflict,
            "PRODUCT_VARIANT_NOT_ACTIVE",
            "Orders can contain only active Product Variants.");

        public static readonly ApplicationErrorDefinition SkuAlreadyExists = new(
            ApplicationErrorKind.Conflict,
            "SKU_ALREADY_EXISTS",
            "The SKU is already in use.");
    }

    public static class Inventories
    {
        public static readonly ApplicationErrorDefinition NotFound = new(
            ApplicationErrorKind.NotFound,
            "INVENTORY_NOT_FOUND",
            "Inventory was not found.");

        public static readonly ApplicationErrorDefinition InvariantViolation = new(
            ApplicationErrorKind.Conflict,
            "INVENTORY_INVARIANT_VIOLATION",
            "The adjustment would leave Inventory in an invalid state.");
    }

    public static class Orders
    {
        public static readonly ApplicationErrorDefinition NotFound = new(
            ApplicationErrorKind.NotFound,
            "ORDER_NOT_FOUND",
            "The Order was not found.");

        public static readonly ApplicationErrorDefinition InsufficientStock = new(
            ApplicationErrorKind.Conflict,
            "INSUFFICIENT_STOCK",
            "Insufficient inventory.");

        public static readonly ApplicationErrorDefinition NotCancellable = new(
            ApplicationErrorKind.Conflict,
            "ORDER_NOT_CANCELLABLE",
            "The Order cannot be cancelled in its current status.");
    }
}
