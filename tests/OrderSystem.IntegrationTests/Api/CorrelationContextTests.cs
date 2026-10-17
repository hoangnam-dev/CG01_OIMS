using Microsoft.AspNetCore.Http;
using OrderSystem.Api.Diagnostics;

namespace OrderSystem.IntegrationTests.Api;

public sealed class CorrelationContextTests
{
    [Fact]
    public void CorrelationId_ResolvedTraceIdentifier_ReturnsSameUuid()
    {
        var expectedCorrelationId =
            Guid.Parse("0199d28b-79a0-7c31-81be-b783e917d465");
        var httpContext = new DefaultHttpContext
        {
            TraceIdentifier = expectedCorrelationId.ToString("D")
        };
        var correlationContext =
            new HttpCorrelationContext(
                new HttpContextAccessor
                {
                    HttpContext = httpContext
                });

        var actualCorrelationId =
            correlationContext.CorrelationId;

        Assert.Equal(
            expectedCorrelationId,
            actualCorrelationId);
    }
}
