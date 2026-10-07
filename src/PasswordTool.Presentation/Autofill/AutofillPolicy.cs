using PasswordTool.Core.Models;
using PasswordTool.Autofill;

namespace PasswordTool.Presentation.Autofill;

public static class AutofillPolicy
{
    public static bool Matches(AutofillCredential credential, string origin) =>
        string.IsNullOrWhiteSpace(credential.Url)
        || CanonicalOrigin.TryParse(credential.Url, out var actual) && actual == origin;

    public static string RequireOrigin(string origin)
    {
        if (!CanonicalOrigin.IsCanonical(origin) && !CanonicalOrigin.IsCanonicalLocalHttp(origin))
            throw new ArgumentException("A canonical HTTPS origin or local HTTP origin is required.");
        return origin;
    }
}
