using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Limits;

internal sealed record UnetAccountBalance(
    string Username,
    decimal? Balance,
    string Currency,
    bool IsAvailable,
    string? Error);

internal sealed record UnetBalanceSnapshot(
    IReadOnlyList<UnetAccountBalance> Accounts,
    DateTimeOffset Timestamp);

internal sealed record UnetBalanceReadResult(UnetBalanceSnapshot? Snapshot, string? ErrorMessage);

internal sealed record UnetCredential(string Username, string Password);

internal sealed class UnetCredentialStore
{
    public string CredentialsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "limits",
        "unet-credentials.json");

    public IReadOnlyList<UnetCredential> Load()
    {
        if (!File.Exists(CredentialsPath))
        {
            return [];
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(CredentialsPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("accounts", out JsonElement accountsElement) ||
                accountsElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            List<UnetCredential> credentials = [];
            foreach (JsonElement accountElement in accountsElement.EnumerateArray())
            {
                string? username = ReadString(accountElement, "username");
                string? protectedPassword = ReadString(accountElement, "passwordProtected");
                if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(protectedPassword))
                {
                    continue;
                }

                byte[] encryptedPassword = Convert.FromBase64String(protectedPassword);
                try
                {
                    byte[] clearPassword = ProtectedData.Unprotect(
                        encryptedPassword,
                        optionalEntropy: null,
                        DataProtectionScope.CurrentUser);
                    try
                    {
                        string password = Encoding.UTF8.GetString(clearPassword);
                        if (!string.IsNullOrWhiteSpace(password))
                        {
                            credentials.Add(new UnetCredential(username.Trim(), password));
                        }
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(clearPassword);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(encryptedPassword);
                }
            }

            return credentials;
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            JsonException or
            FormatException or
            CryptographicException)
        {
            throw new InvalidOperationException("UNET credential storage could not be read.", exception);
        }
    }

    private static string? ReadString(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }
}

internal sealed class UnetBalanceReader
{
    private static readonly Uri LoginUri = new("https://my.unet.by/login");
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private const string Currency = "BYN";
    private readonly UnetCredentialStore _credentialStore = new();

    public string CredentialsPath => _credentialStore.CredentialsPath;

    public UnetBalanceReadResult ReadLatestSnapshot()
    {
        try
        {
            IReadOnlyList<UnetCredential> credentials = _credentialStore.Load();
            if (credentials.Count == 0)
            {
                throw new InvalidOperationException($"No UNET credentials were found at {CredentialsPath}.");
            }

            List<UnetAccountBalance> accounts = [];
            foreach (UnetCredential credential in credentials)
            {
                try
                {
                    decimal balance = ReadAccountBalance(credential);
                    accounts.Add(new UnetAccountBalance(credential.Username, balance, Currency, true, null));
                }
                catch (Exception exception) when (IsAccountReadFailure(exception))
                {
                    accounts.Add(new UnetAccountBalance(
                        credential.Username,
                        null,
                        Currency,
                        false,
                        GetSafeErrorMessage(exception)));
                }
            }

            return new UnetBalanceReadResult(
                new UnetBalanceSnapshot(accounts, DateTimeOffset.UtcNow),
                null);
        }
        catch (Exception exception) when (exception is
            HttpRequestException or
            TaskCanceledException or
            IOException or
            JsonException or
            InvalidOperationException or
            UnauthorizedAccessException or
            FormatException or
            CryptographicException)
        {
            return new UnetBalanceReadResult(null, GetSafeErrorMessage(exception));
        }
    }

    private static decimal ReadAccountBalance(UnetCredential credential)
    {
        using HttpClientHandler handler = new()
        {
            AllowAutoRedirect = true,
            UseCookies = true,
            CookieContainer = new CookieContainer()
        };
        using HttpClient client = new(handler)
        {
            Timeout = RequestTimeout
        };
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("limits/1.3");

        using HttpRequestMessage pageRequest = new(HttpMethod.Get, LoginUri);
        using HttpResponseMessage pageResponse = client.Send(pageRequest);
        if (!pageResponse.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"UNET login page returned HTTP {(int)pageResponse.StatusCode}.");
        }

        string loginHtml = pageResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        string csrfToken = ReadInputValue(loginHtml, "_csrf_token")
            ?? throw new InvalidOperationException("UNET login form did not include a CSRF token.");

        using FormUrlEncodedContent form = new(new Dictionary<string, string>
        {
            ["username"] = credential.Username,
            ["password"] = credential.Password,
            ["_csrf_token"] = csrfToken
        });
        using HttpRequestMessage loginRequest = new(HttpMethod.Post, LoginUri)
        {
            Content = form
        };
        loginRequest.Headers.Referrer = LoginUri;

        using HttpResponseMessage accountResponse = client.Send(loginRequest);
        if (!accountResponse.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"UNET login returned HTTP {(int)accountResponse.StatusCode}.");
        }

        string accountHtml = accountResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        return ParseBalance(accountHtml);
    }

    private static decimal ParseBalance(string html)
    {
        int balanceLabelIndex = html.IndexOf("Текущий баланс", StringComparison.OrdinalIgnoreCase);
        if (balanceLabelIndex < 0)
        {
            throw new InvalidOperationException("UNET login was not accepted or the balance row changed.");
        }

        int rowEnd = html.IndexOf("</tr>", balanceLabelIndex, StringComparison.OrdinalIgnoreCase);
        int spanStart = html.IndexOf("<span", balanceLabelIndex, StringComparison.OrdinalIgnoreCase);
        if (rowEnd < 0 || spanStart < 0 || spanStart >= rowEnd)
        {
            throw new InvalidOperationException("UNET balance value was not found.");
        }

        int spanEnd = html.IndexOf('>', spanStart);
        if (spanEnd < 0 || spanEnd >= rowEnd)
        {
            throw new InvalidOperationException("UNET balance value was not found.");
        }

        string? balanceText = ReadAttribute(html[spanStart..(spanEnd + 1)], "title");
        if (decimal.TryParse(balanceText, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal balance))
        {
            return balance;
        }

        throw new InvalidOperationException("UNET balance value was invalid.");
    }

    private static string? ReadInputValue(string html, string inputName)
    {
        int searchIndex = 0;
        while (searchIndex < html.Length)
        {
            int inputStart = html.IndexOf("<input", searchIndex, StringComparison.OrdinalIgnoreCase);
            if (inputStart < 0)
            {
                return null;
            }

            int inputEnd = html.IndexOf('>', inputStart);
            if (inputEnd < 0)
            {
                return null;
            }

            string inputTag = html[inputStart..(inputEnd + 1)];
            if (string.Equals(ReadAttribute(inputTag, "name"), inputName, StringComparison.OrdinalIgnoreCase))
            {
                return ReadAttribute(inputTag, "value");
            }

            searchIndex = inputEnd + 1;
        }

        return null;
    }

    private static string? ReadAttribute(string tag, string attributeName)
    {
        int searchIndex = 0;
        while (searchIndex < tag.Length)
        {
            int attributeStart = tag.IndexOf(attributeName, searchIndex, StringComparison.OrdinalIgnoreCase);
            if (attributeStart < 0)
            {
                return null;
            }

            int attributeEnd = attributeStart + attributeName.Length;
            bool validStart = attributeStart == 0 || !IsAttributeNameCharacter(tag[attributeStart - 1]);
            bool validEnd = attributeEnd >= tag.Length || !IsAttributeNameCharacter(tag[attributeEnd]);
            if (!validStart || !validEnd)
            {
                searchIndex = attributeEnd;
                continue;
            }

            int equalsIndex = attributeEnd;
            while (equalsIndex < tag.Length && char.IsWhiteSpace(tag[equalsIndex]))
            {
                equalsIndex++;
            }

            if (equalsIndex >= tag.Length || tag[equalsIndex] != '=')
            {
                searchIndex = attributeEnd;
                continue;
            }

            int valueStart = equalsIndex + 1;
            while (valueStart < tag.Length && char.IsWhiteSpace(tag[valueStart]))
            {
                valueStart++;
            }

            if (valueStart >= tag.Length)
            {
                return null;
            }

            char quote = tag[valueStart];
            if (quote is '"' or '\'')
            {
                int valueEnd = tag.IndexOf(quote, valueStart + 1);
                return valueEnd < 0
                    ? null
                    : WebUtility.HtmlDecode(tag[(valueStart + 1)..valueEnd]);
            }

            int unquotedEnd = valueStart;
            while (unquotedEnd < tag.Length && !char.IsWhiteSpace(tag[unquotedEnd]) && tag[unquotedEnd] != '>')
            {
                unquotedEnd++;
            }

            return WebUtility.HtmlDecode(tag[valueStart..unquotedEnd]);
        }

        return null;
    }

    private static bool IsAttributeNameCharacter(char value)
    {
        return char.IsLetterOrDigit(value) || value is '_' or '-' or ':';
    }

    private static bool IsAccountReadFailure(Exception exception)
    {
        return exception is
            HttpRequestException or
            TaskCanceledException or
            IOException or
            InvalidOperationException or
            FormatException;
    }

    private static string GetSafeErrorMessage(Exception exception)
    {
        return exception switch
        {
            TaskCanceledException => "UNET request timed out.",
            HttpRequestException => "UNET HTTP request failed.",
            _ => "UNET login or balance parsing failed."
        };
    }
}
