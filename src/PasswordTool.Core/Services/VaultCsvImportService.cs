using System.Text;
using PasswordTool.Core.Models;

namespace PasswordTool.Core.Services;

public sealed class VaultCsvImportService
{
    public const int MaxCsvCharacters = 10 * 1024 * 1024;
    public const int MaxRows = 10_000;

    private const int MaxColumns = 64;
    private const int MaxFieldLength = 100_000;

    private readonly TotpService totpService;

    public VaultCsvImportService(TotpService? totpService = null)
    {
        this.totpService = totpService ?? new TotpService();
    }

    public IReadOnlyList<VaultItem> Parse(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            throw new InvalidDataException("The CSV file is empty.");
        }

        if (csv.Length > MaxCsvCharacters)
        {
            throw new InvalidDataException("The CSV file exceeds the 10 MB limit.");
        }

        var rows = ParseRows(csv);
        if (rows.Count < 2)
        {
            throw new InvalidDataException("The CSV file must contain a header and at least one account row.");
        }

        var header = rows[0].Select(NormalizeHeader).ToList();
        EnsureUniqueHeaders(header);
        var titleIndex = FindHeader(header, "name", "title");
        var passwordIndex = FindHeader(header, "password", "loginpassword");
        var totpIndex = FindHeader(header, "totp", "logintotp", "otp", "otpauth");
        if (titleIndex < 0 || passwordIndex < 0 && totpIndex < 0)
        {
            throw new InvalidDataException("The CSV header must include Name or Title, and Password or TOTP.");
        }

        var usernameIndex = FindHeader(header, "username", "loginusername", "user");
        var urlIndex = FindHeader(header, "url", "loginuri", "website");
        var notesIndex = FindHeader(header, "notes", "note");
        var folderIndex = FindHeader(header, "folder", "foldername");
        var favoriteIndex = FindHeader(header, "favorite", "favourite");
        var tagsIndex = FindHeader(header, "tags", "tag");
        var typeIndex = FindHeader(header, "type");

        var items = new List<VaultItem>();
        for (var rowIndex = 1; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            if (row.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            if (typeIndex >= 0)
            {
                var type = GetField(row, typeIndex).Trim();
                if (type.Length > 0 && !type.Equals("login", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            var password = GetField(row, passwordIndex);
            var rawTotp = GetField(row, totpIndex).Trim();
            if (string.IsNullOrEmpty(password) && rawTotp.Length == 0)
            {
                continue;
            }

            var title = GetField(row, titleIndex).Trim();
            var username = GetField(row, usernameIndex).Trim();
            var url = GetField(row, urlIndex).Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                title = !string.IsNullOrWhiteSpace(url) ? url : username;
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                title = $"Imported account {rowIndex}";
            }

            TotpConfiguration? configuration = null;
            if (rawTotp.Length > 0)
            {
                if (!totpService.TryParseWebsiteConfiguration(rawTotp, out configuration, out _))
                {
                    throw new InvalidDataException($"CSV row {rowIndex + 1} contains an invalid TOTP secret.");
                }
            }

            var item = new VaultItem
            {
                Id = Guid.NewGuid(),
                Type = VaultItemType.Password,
                Title = title,
                Username = username,
                Password = password,
                Url = url,
                Notes = GetField(row, notesIndex),
                LegacyFolder = GetField(row, folderIndex).Trim(),
                IsFavorite = ParseBoolean(GetField(row, favoriteIndex)),
                Tags = ParseTags(GetField(row, tagsIndex))
            };
            item.SetTotpConfiguration(configuration);
            VaultBackupService.ValidateItems([item]);
            items.Add(item);
        }

        if (items.Count == 0)
        {
            throw new InvalidDataException("The CSV file did not contain any password or TOTP entries that can be imported.");
        }

        return items;
    }

    public VaultCsvImportPlan CreateImportPlan(IReadOnlyList<VaultItem> importedItems, IReadOnlyList<VaultItem> existingItems)
    {
        ArgumentNullException.ThrowIfNull(importedItems);
        ArgumentNullException.ThrowIfNull(existingItems);
        var planItems = importedItems.Select(item => new VaultCsvImportPlanItem(
            item.Title,
            item.Username,
            item.Url,
            existingItems.Any(existing => AreEquivalentAccount(existing, item))
                ? VaultCsvImportStatus.Duplicate
                : VaultCsvImportStatus.New)).ToList();
        return new VaultCsvImportPlan(planItems);
    }

    public IReadOnlyList<VaultItem> SelectNewItems(IReadOnlyList<VaultItem> importedItems, IReadOnlyList<VaultItem> existingItems)
    {
        return importedItems.Where(item => !existingItems.Any(existing => AreEquivalentAccount(existing, item))).ToList();
    }

    private static List<List<string>> ParseRows(string csv)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var insideQuotes = false;

        for (var index = 0; index < csv.Length; index++)
        {
            var character = csv[index];
            if (insideQuotes)
            {
                if (character == '"' && index + 1 < csv.Length && csv[index + 1] == '"')
                {
                    AppendFieldCharacter(field, '"');
                    index++;
                }
                else if (character == '"')
                {
                    insideQuotes = false;
                }
                else
                {
                    AppendFieldCharacter(field, character);
                }
                continue;
            }

            if (character == '"' && field.Length == 0)
            {
                insideQuotes = true;
            }
            else if (character == ',')
            {
                AddField(row, field);
            }
            else if (character is '\r' or '\n')
            {
                if (character == '\r' && index + 1 < csv.Length && csv[index + 1] == '\n') index++;
                AddField(row, field);
                AddRow(rows, row);
                row = [];
            }
            else
            {
                AppendFieldCharacter(field, character);
            }
        }

        if (insideQuotes)
        {
            throw new InvalidDataException("The CSV file contains an unterminated quoted field.");
        }

        if (field.Length > 0 || row.Count > 0)
        {
            AddField(row, field);
            AddRow(rows, row);
        }

        return rows;
    }

    private static void AppendFieldCharacter(StringBuilder field, char character)
    {
        if (field.Length >= MaxFieldLength)
        {
            throw new InvalidDataException("A CSV field exceeds the 100,000-character limit.");
        }
        field.Append(character);
    }

    private static void AddField(List<string> row, StringBuilder field)
    {
        if (row.Count >= MaxColumns)
        {
            throw new InvalidDataException($"A CSV row exceeds the {MaxColumns}-column limit.");
        }
        row.Add(field.ToString());
        field.Clear();
    }

    private static void AddRow(List<List<string>> rows, List<string> row)
    {
        if (rows.Count >= MaxRows + 1)
        {
            throw new InvalidDataException($"The CSV file exceeds the {MaxRows:N0}-row limit.");
        }
        rows.Add(row);
    }

    private static string NormalizeHeader(string value) => new(value.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static void EnsureUniqueHeaders(IReadOnlyList<string> headers)
    {
        if (headers.Count > MaxColumns || headers.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException("The CSV header contains an empty or unsupported column.");
        }

        if (headers.Count != headers.Distinct(StringComparer.Ordinal).Count())
        {
            throw new InvalidDataException("The CSV header contains duplicate columns.");
        }
    }

    private static int FindHeader(IReadOnlyList<string> headers, params string[] names)
    {
        for (var index = 0; index < headers.Count; index++)
        {
            if (names.Contains(headers[index], StringComparer.Ordinal)) return index;
        }
        return -1;
    }

    private static string GetField(IReadOnlyList<string> row, int index) => index >= 0 && index < row.Count ? row[index] : string.Empty;

    private static bool ParseBoolean(string value) => value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)
        || value.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase)
        || value.Trim() == "1";

    private static List<string> ParseTags(string value) => value
        .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(tag => tag.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(20)
        .ToList();

    private static bool AreEquivalentAccount(VaultItem left, VaultItem right) => left.Type == VaultItemType.Password
        && right.Type == VaultItemType.Password
        && string.Equals(left.Title, right.Title, StringComparison.OrdinalIgnoreCase)
        && left.Username == right.Username
        && left.Password == right.Password
        && left.GetTotpConfiguration() == right.GetTotpConfiguration()
        && string.Equals(left.Url, right.Url, StringComparison.OrdinalIgnoreCase);
}
