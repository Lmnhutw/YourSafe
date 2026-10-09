using PasswordTool.Core.Models;

namespace PasswordTool.Presentation.Autofill;

public enum AutofillAction { Password, ViewTotp, CopyTotp }

// Vault-derived identity; Origin is a claim from the extension, not browser attestation.
public sealed record AutofillApprovalRequest(AutofillCredential Credential, AutofillAction Action, string Origin);
