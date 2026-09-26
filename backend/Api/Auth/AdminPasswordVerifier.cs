using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace Goulash.Api.Auth;

public sealed class AdminPasswordVerifier
{
    private readonly byte[] _passwordHash;

    public AdminPasswordVerifier(string password) => _passwordHash = SHA256.HashData(Encoding.UTF8.GetBytes(password));

    public bool IsValid(string candidate)
    {
        var candidateHash = SHA256.HashData(Encoding.UTF8.GetBytes(candidate));
        return CryptographicOperations.FixedTimeEquals(_passwordHash, candidateHash);
    }

    public static ClaimsIdentity CreateIdentity() => new(
        [new Claim(ClaimTypes.NameIdentifier, "admin"), new Claim(ClaimTypes.Role, "administrator")],
        "admin-password");
}

public sealed record LoginRequest(string? Password);
