using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using OrderSystem.Api.Contracts;
using OrderSystem.Api.Errors;
using OrderSystem.Api.Payments;
using OrderSystem.Application.Common.Clock;
using OrderSystem.Application.Common.Results;
using OrderSystem.Application.Payments;
using OrderSystem.Application.Payments.Contracts;

namespace OrderSystem.Api.Endpoints;

public static class FakePaymentWebhookEndpoints
{
    private const string TimestampHeaderName = "X-Fake-Webhook-Timestamp";
    private const string SignatureHeaderName = "X-Fake-Webhook-Signature";
    private const int MaximumBodySize = 16 * 1024;

    public static IEndpointRouteBuilder MapFakePaymentWebhookEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/payments/webhooks/fake", HandleAsync)
            .AllowAnonymous()
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status413PayloadTooLarge);

        return endpoints;
    }

    private static async Task<IResult> HandleAsync(
        HttpRequest request,
        FakeWebhookSignatureValidator signatureValidator,
        PaymentResultApplicationService resultApplicationService,
        IOptions<JsonOptions> jsonOptions,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength is > MaximumBodySize)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var body = await ReadBodyAsync(request.Body, cancellationToken);
        if (body is null)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var timestamp = request.Headers[TimestampHeaderName].ToString();
        var signature = request.Headers[SignatureHeaderName].ToString();

        if (!signatureValidator.IsValid(timestamp, signature, body, clock.UtcNow))
        {
            return ApplicationResultHttpMapper.ToProblem(
                request.HttpContext,
                ApplicationErrors.Unauthorized.Create()
            );
        }

        FakePaymentWebhookRequest? webhook;
        try
        {
            webhook = JsonSerializer.Deserialize<FakePaymentWebhookRequest>(body, jsonOptions.Value.SerializerOptions);
        }
        catch (JsonException)
        {
            return ValidationProblem(request);
        }

        if (webhook is null ||
            string.IsNullOrWhiteSpace(webhook.ProviderEventId) ||
            string.IsNullOrWhiteSpace(webhook.ProviderPaymentId) ||
            string.IsNullOrWhiteSpace(webhook.EventType) ||
            webhook.OccurredAt == default)
        {
            return ValidationProblem(request);
        }

        if (!PaymentResultClassifier.TryClassify(webhook.Status, out var providerOutcome))
        {
            return ValidationProblem(request);
        }
        if (!FakePaymentWebhookEventClassifier.TryClassify(webhook.EventType, out var eventOutcome) ||
            eventOutcome != providerOutcome)
        {
            return ValidationProblem(request);
        }

        if (providerOutcome == ProviderPaymentOutcome.Failed &&
            string.IsNullOrWhiteSpace(webhook.FailureCode))
        {
            return ValidationProblem(request);
        }

        if (providerOutcome == ProviderPaymentOutcome.Succeeded &&
            webhook.FailureCode is not null)
        {
            return ValidationProblem(request);
        }

        var payloadHash = Convert.ToHexStringLower(SHA256.HashData(body));
        var command = new ApplyPaymentResultCommand(
            webhook.ProviderPaymentId,
            providerOutcome,
            webhook.FailureCode,
            PaymentResultSource.Webhook,
            new ProviderPaymentEventData(
                PaymentProviderCodes.Fake,
                webhook.ProviderEventId,
                webhook.EventType,
                payloadHash),
            webhook.OccurredAt);

        var outcome = await resultApplicationService.ApplyAsync(
            command,
            cancellationToken);

        return outcome.Status switch
        {
            PaymentResultApplicationStatus.Accepted => Results.Ok(),
            PaymentResultApplicationStatus.Duplicate => Results.Ok(),
            PaymentResultApplicationStatus.PaymentNotFound => ApplicationResultHttpMapper.ToProblem(
                request.HttpContext,
                ApplicationErrors.Payments.NotFound.Create()
            ),
            PaymentResultApplicationStatus.EventConflict => ApplicationResultHttpMapper.ToProblem(
                request.HttpContext,
                ApplicationErrors.Payments.EventConflict.Create()
            ),
            _ => throw new InvalidOperationException(
                "Unsupported payment result application status")
        };
    }

    private static async Task<byte[]?> ReadBodyAsync(Stream requestBody, CancellationToken cancellationToken)
    {
        using var bodyStream = new MemoryStream(MaximumBodySize + 1);
        var buffer = new byte[8192];

        while (bodyStream.Length <= MaximumBodySize)
        {
            var remaining = MaximumBodySize + 1 - (int)bodyStream.Length;
            var bytesRead = await requestBody.ReadAsync(
                buffer.AsMemory(0, Math.Min(buffer.Length, remaining)),
                cancellationToken);

            if (bytesRead == 0)
            {
                break;
            }

            await bodyStream.WriteAsync(
                buffer.AsMemory(0, bytesRead),
                cancellationToken);
        }

        return bodyStream.Length > MaximumBodySize
            ? null
            : bodyStream.ToArray();
    }

    private static IResult ValidationProblem(HttpRequest request) =>
    ApplicationResultHttpMapper.ToProblem(
        request.HttpContext,
        ApplicationErrors.ValidationFailed.Create());
}