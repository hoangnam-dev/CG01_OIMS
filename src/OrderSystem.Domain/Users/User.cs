namespace OrderSystem.Domain.Users;

public sealed class User
{
    private User()
    {
    }

    public User(
        Guid id,
        string email,
        string normalizedEmail,
        string passwordHash,
        UserRole role,
        DateTimeOffset createdAt)
    {
        Id = id;
        Email = email;
        NormalizedEmail = normalizedEmail;
        PasswordHash = passwordHash;
        Role = role;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public string Email { get; private set; } = string.Empty;

    public string NormalizedEmail { get; private set; } = string.Empty;

    public string PasswordHash { get; private set; } = string.Empty;

    public UserRole Role { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }
}
