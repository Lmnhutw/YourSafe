using System.Text.Json;
using System.Text.Json.Serialization;

namespace PasswordTool.Autofill;

public static class CanonicalOrigin
{
    public static bool TryParse(string? value, out string origin)
    {
        origin = "";
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Contains('\\')
            || !(value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.UserInfo.Length != 0 || string.IsNullOrEmpty(uri.Host)) return false;
        var localHttp = uri.Scheme == "http" && (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host is "127.0.0.1" or "::1" or "[::1]");
        if (uri.Scheme != "https" && !localHttp) return false;
        var authority = value[(value.IndexOf("//", StringComparison.Ordinal) + 2)..].Split('/', '?', '#')[0];
        if (authority.Contains('%')) return false;
        string host;
        try { host = uri.IdnHost.ToLowerInvariant(); }
        catch (UriFormatException) { return false; }
        if (uri.HostNameType == UriHostNameType.IPv6) host = "[" + host.Trim('[', ']') + "]";
        // Reject nonstandard numeric IPv4 forms, which browsers and System.Uri can interpret differently.
        if (uri.HostNameType == UriHostNameType.IPv4 && System.Net.IPAddress.TryParse(host, out var address))
        {
            var addressAuthority = authority.Split(':')[0];
            if (addressAuthority != address.ToString()) return false;
            host = address.ToString();
        }
        var defaultPort = uri.Scheme == "https" ? 443 : 80;
        origin = uri.Scheme + "://" + host + (uri.Port == defaultPort ? "" : ":" + uri.Port);
        return true;
    }

    public static bool IsCanonical(string? value) => TryParse(value, out var origin) && origin == value;

    public static bool IsCanonicalLocalHttp(string? value) => TryParse(value, out var origin) && origin == value
        && value.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
}

public sealed record AutofillPayload(string? Origin = null, Guid? CredentialId = null);
public sealed record AutofillRequest(int Version, string RequestId, string Action, AutofillPayload Payload);
public sealed record CredentialMetadata(Guid Id, string Title, string Username);
public sealed record DiscoveryResult(IReadOnlyList<CredentialMetadata> Credentials, bool Truncated);
public sealed record CredentialSecret(string Username, string Password)
{
    public override string ToString() => "CredentialSecret { redacted }";
}
public sealed record StatusResult(bool Unlocked);
public sealed record PingResult(string Host);
public sealed record ShowAppResult(bool Shown);
public sealed record AutofillResponse(int Version, string RequestId, bool Ok, JsonElement? Result = null, string? Error = null);

public static class WireProtocol
{
    public const int Version = 1;
    public const int MaxFrameBytes = 64 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static AutofillRequest ParseRequest(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxFrameBytes) throw new InvalidDataException("Frame too large.");
        using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("An object envelope is required.");
        RejectDuplicateKeys(document.RootElement);
        if (document.RootElement.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object
            && payload.EnumerateObject().Any(property => property.Value.ValueKind == JsonValueKind.Null))
            throw new JsonException("Payload fields must be omitted rather than null.");
        var request = document.RootElement.Deserialize<AutofillRequest>(Json) ?? throw new JsonException();
        Validate(request);
        return request;
    }

    public static void Validate(AutofillRequest request)
    {
        if (request.Version != Version || request.RequestId?.Length != 36 || !Guid.TryParseExact(request.RequestId, "D", out _)
            || request.Payload is null) throw new JsonException("Invalid envelope.");
        var hasOrigin = request.Action is "findCredentials" or "getCredentialSecret";
        if (request.Action is not ("ping" or "getStatus" or "findCredentials" or "getCredentialSecret" or "showApp")
            || (hasOrigin ? !CanonicalOrigin.IsCanonical(request.Payload.Origin) : request.Payload.Origin is not null)
            || (request.Action == "getCredentialSecret"
                ? request.Payload.CredentialId is null || request.Payload.CredentialId == Guid.Empty
                : request.Payload.CredentialId is not null)) throw new JsonException("Invalid action payload.");
    }

    public static AutofillResponse Success<T>(string requestId, T result) =>
        new(Version, requestId, true, JsonSerializer.SerializeToElement(result, Json), null);
    public static AutofillResponse Failure(string requestId, string code) => new(Version, requestId, false, null, code);

    public static AutofillResponse DiscoverySuccess(string requestId, IReadOnlyList<CredentialMetadata> credentials)
    {
        var included = new List<CredentialMetadata>();
        var size = JsonSerializer.SerializeToUtf8Bytes(Success(requestId, new DiscoveryResult(included, false)), Json).Length;
        foreach (var credential in credentials)
        {
            var itemSize = JsonSerializer.SerializeToUtf8Bytes(credential, Json).Length + (included.Count > 0 ? 1 : 0);
            if (size + itemSize > MaxFrameBytes) break;
            included.Add(credential);
            size += itemSize;
        }
        // ponytail: one bounded page; add pagination if users need every matching account in the popup.
        return Success(requestId, new DiscoveryResult(included, included.Count < credentials.Count));
    }

    public static AutofillResponse ParseResponse(byte[] bytes, AutofillRequest request)
    {
        if (bytes.Length > MaxFrameBytes) throw new InvalidDataException("Frame too large.");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("An object envelope is required.");
        RejectDuplicateKeys(document.RootElement);
        if (document.RootElement.EnumerateObject().Any(property => property.Value.ValueKind == JsonValueKind.Null))
            throw new JsonException("Response fields must be omitted rather than null.");
        var response = document.RootElement.Deserialize<AutofillResponse>(Json) ?? throw new JsonException();
        if (response.Version != Version || response.RequestId != request.RequestId) throw new JsonException("Invalid response envelope.");
        if (!response.Ok)
        {
            if (response.Result is not null || response.Error is not ("locked" or "unavailable" or "desktopUnavailable" or "invalidRequest"))
                throw new JsonException("Invalid error response.");
            return response;
        }
        if (response.Error is not null || response.Result is not { } result) throw new JsonException("Missing result.");
        var valid = request.Action switch
        {
            "ping" => result.Deserialize<PingResult>(Json)?.Host == "YourSafe",
            "getStatus" => result.Deserialize<StatusResult>(Json) is not null,
            "showApp" => result.Deserialize<ShowAppResult>(Json) is not null,
            "findCredentials" => result.Deserialize<DiscoveryResult>(Json) is { Credentials: not null } discovery
                && discovery.Credentials.All(item => item is not null && item.Id != Guid.Empty && item.Title is not null && item.Username is not null),
            "getCredentialSecret" => result.Deserialize<CredentialSecret>(Json) is { Username: not null, Password: not null },
            _ => false
        };
        if (!valid) throw new JsonException("Invalid result.");
        return response;
    }

    private static void RejectDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name)) throw new JsonException("Duplicate property.");
            RejectDuplicateKeys(property.Value);
        }
    }
}
