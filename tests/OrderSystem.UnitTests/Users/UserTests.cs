using OrderSystem.Domain.Users;

namespace OrderSystem.UnitTests.Users;

public sealed class UserTests
{
    [Fact]
    public void Constructor_ValidIdentity_PreservesFoundationFields()
    {
        var id = Guid.NewGuid();
        var createdAt = new DateTimeOffset(2026, 9, 16, 8, 0, 0, TimeSpan.Zero);

        var user = new User(
            id,
            "customer@example.com",
            "customer@example.com",
            Authentication.TestCredentials.CreateHashPlaceholder(),
            UserRole.Customer,
            createdAt);

        Assert.Equal(id, user.Id);
        Assert.Equal("customer@example.com", user.Email);
        Assert.Equal("customer@example.com", user.NormalizedEmail);
        Assert.Equal(UserRole.Customer, user.Role);
        Assert.Equal(createdAt, user.CreatedAt);
        Assert.Equal(createdAt, user.UpdatedAt);
    }
}
