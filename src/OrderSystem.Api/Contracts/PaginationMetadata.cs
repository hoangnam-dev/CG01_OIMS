namespace OrderSystem.Api.Contracts;

public sealed record PaginationMetadata(int Page, int PageSize, long TotalCount, int TotalPages);
