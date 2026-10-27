using OrderSystem.Infrastructure.Notifications;

namespace OrderSystem.IntegrationTests.Notifications;

public sealed class FailedMessageBodyFingerprintTests
{
    [Fact]
    [Trait("Requirement", "DB-MSG-002")]
    public void Compute_EmptyBody_ReturnsLowercaseSha256()
    {
        var fingerprint = FailedMessageBodyFingerprint.Compute([]);

        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            fingerprint);
    }
}
