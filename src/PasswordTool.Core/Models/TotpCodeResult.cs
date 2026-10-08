namespace PasswordTool.Core.Models;

public sealed record TotpCodeResult(
    string Code,
    int SecondsRemaining,
    int PeriodSeconds = 30,
    DateTimeOffset ExpiresAtUtc = default,
    string Issuer = "",
    string AccountName = "")
{
    public override string ToString() => "TotpCodeResult { Code = [REDACTED] }";
}
