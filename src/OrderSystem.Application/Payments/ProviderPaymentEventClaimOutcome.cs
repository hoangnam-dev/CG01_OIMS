namespace OrderSystem.Application.Payments;

public enum ProviderPaymentEventClaimOutcome
{
    Claimed = 1,
    Duplicate = 2,
    Conflict = 3
}