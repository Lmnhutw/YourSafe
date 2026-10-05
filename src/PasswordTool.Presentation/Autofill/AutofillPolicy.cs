using PasswordTool.Core.Models;
using PasswordTool.Autofill;

namespace PasswordTool.Presentation.Autofill;

public static class AutofillPolicy
{
    public static bool Matches(AutofillCredential credential, string origin) =>
        CanonicalOrigin.TryParse(credential.Url, out var actual) && actual == origin;

    public static string RequireOrigin(string origin)
    {
        if (!CanonicalOrigin.IsCanonical(origin)) throw new ArgumentException("A canonical HTTPS origin is required.");
        return origin;
    }
}
