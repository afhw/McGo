using System.Text;
using System.Text.Json.Nodes;

namespace McGo.Core.Security;

public static class SecureStore
{
    public static readonly string[] TokenFields = ["access_token", "refresh_token", "client_token"];
    private const string FallbackPrefix = "plain64:";

    public static string ProtectSecret(string? value)
    {
        value ??= "";
        if (value.Length == 0 || value.StartsWith(FallbackPrefix, StringComparison.Ordinal))
        {
            return value;
        }

        return FallbackPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    }

    public static string UnprotectSecret(string? value)
    {
        value ??= "";
        if (!value.StartsWith(FallbackPrefix, StringComparison.Ordinal))
        {
            return value;
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value[FallbackPrefix.Length..]));
        }
        catch (FormatException)
        {
            return "";
        }
    }

    public static JsonObject HydrateAccountTokens(JsonObject account)
    {
        var hydrated = CloneObject(account);
        var secureTokens = hydrated["secure_tokens"] as JsonObject;
        if (secureTokens is null)
        {
            return hydrated;
        }

        foreach (var field in TokenFields)
        {
            if (!string.IsNullOrEmpty(hydrated[field]?.GetValue<string>()))
            {
                continue;
            }

            var protectedValue = secureTokens[field]?.GetValue<string>();
            if (!string.IsNullOrEmpty(protectedValue))
            {
                hydrated[field] = UnprotectSecret(protectedValue);
            }
        }

        return hydrated;
    }

    public static JsonObject RedactAccountTokens(JsonObject account)
    {
        var redacted = CloneObject(account);
        var secureTokens = redacted["secure_tokens"] as JsonObject ?? [];
        foreach (var field in TokenFields)
        {
            var token = redacted[field]?.GetValue<string>() ?? "";
            redacted.Remove(field);
            if (!string.IsNullOrEmpty(token))
            {
                secureTokens[field] = ProtectSecret(token);
            }
        }

        if (secureTokens.Count > 0)
        {
            redacted["secure_tokens"] = secureTokens;
        }
        else
        {
            redacted.Remove("secure_tokens");
        }

        return redacted;
    }

    public static List<JsonObject> HydrateAccounts(IEnumerable<JsonObject> accounts)
        => accounts.Select(HydrateAccountTokens).ToList();

    public static List<JsonObject> RedactAccounts(IEnumerable<JsonObject> accounts)
        => accounts.Select(RedactAccountTokens).ToList();

    private static JsonObject CloneObject(JsonObject account)
        => JsonNode.Parse(account.ToJsonString())!.AsObject();
}
