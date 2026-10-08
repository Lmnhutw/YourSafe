namespace PasswordTool.Core.Models;

public enum TotpAlgorithm
{
    Sha1,
    Sha256,
    Sha512
}

public sealed record TotpConfiguration(
    string Secret,
    string Issuer = "",
    string AccountName = "",
    TotpAlgorithm Algorithm = TotpAlgorithm.Sha1,
    int Digits = 6,
    int Period = 30)
{
    public override string ToString() => "TotpConfiguration { Secret = [REDACTED] }";
}
