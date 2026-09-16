namespace OrderSystem.Api.Contracts;

public sealed record ApiResponse<TData>(TData Data, ApiMetadata? Metadata);
