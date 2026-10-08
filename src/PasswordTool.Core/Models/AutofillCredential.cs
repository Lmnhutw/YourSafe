namespace PasswordTool.Core.Models;

// Application projection; never send this URL-bearing type over a transport.
public sealed record AutofillCredential(Guid Id, string Title, string Username, string Url,
    bool HasPassword = true, bool HasTotp = false);
public sealed record AutofillSecret(string Username, string Password)
{
    public override string ToString() => "AutofillSecret { redacted }";
}
