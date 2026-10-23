namespace PasswordTool.Core.Models;

public sealed class VaultData
{
    public int Version { get; set; } = 3;

    public List<VaultItem> Items { get; set; } = [];

    public List<VaultGroup> Groups { get; set; } = [];
}
