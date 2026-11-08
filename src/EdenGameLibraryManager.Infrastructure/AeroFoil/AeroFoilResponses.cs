using System.Net;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using EdenGameLibraryManager.Core.Models;

namespace EdenGameLibraryManager.Infrastructure.AeroFoil;

/// <summary>
/// Maps AeroFoil responses to <see cref="AeroFoilException"/>s. Note that AeroFoil reports most
/// authentication failures as HTTP 200 with a JSON body of the form {"error": "..."}, so a
/// successful status code alone does not mean the request was authorised.
/// </summary>
internal static class AeroFoilResponses
{
    public const string ClientBlockedMessage =
        "This AeroFoil server only accepts Tinfoil/CyberFoil clients from external networks. " +
        "Eden Game Library Manager does not impersonate those clients. Ask the server administrator to turn off " +
        "\"External access: Tinfoil/CyberFoil only\" in AeroFoil Settings > Shop, or connect over your LAN/VPN.";

    public const string EncryptedMessage =
        "The server sends an encrypted catalogue (AeroFoil \"Encrypt shop\" is enabled), which only Tinfoil can read. " +
        "Ask the server administrator to disable \"Encrypt shop\" in AeroFoil Settings > Shop.";

    private const int MaxErrorBodyBytes = 64 * 1024;

    public static async Task ThrowForStatusAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        var code = (int)response.StatusCode;
        var message = await TryReadErrorMessageAsync(response, ct);

        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new AeroFoilException(AeroFoilErrorKind.InvalidCredentials,
                "The server rejected the username or password."),
            HttpStatusCode.Forbidden when message?.Contains("Tinfoil/CyberFoil", StringComparison.OrdinalIgnoreCase) == true
                => new AeroFoilException(AeroFoilErrorKind.ClientBlocked, ClientBlockedMessage),
            HttpStatusCode.Forbidden => new AeroFoilException(AeroFoilErrorKind.AccessDenied,
                string.IsNullOrWhiteSpace(message) ? "Your account is not allowed to access this content." : message!),
            HttpStatusCode.NotFound => new AeroFoilException(AeroFoilErrorKind.NotFound, "The requested item was not found on the server."),
            HttpStatusCode.TooManyRequests => new AeroFoilException(AeroFoilErrorKind.RateLimited,
                "The server is rate limiting requests. Wait a few minutes and try again."),
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => new AeroFoilException(AeroFoilErrorKind.Timeout,
                $"The server timed out (HTTP {code})."),
            _ when code >= 500 => new AeroFoilException(AeroFoilErrorKind.ServerError, $"The server reported an error (HTTP {code})."),
            _ => new AeroFoilException(AeroFoilErrorKind.InvalidResponse, $"Unexpected server response (HTTP {code})."),
        };
    }

    /// <summary>Maps AeroFoil's textual error messages (from auth.basic_auth and tinfoil_access) to error kinds.</summary>
    public static AeroFoilException FromErrorMessage(string message)
    {
        var m = message.Trim();
        if (Contains(m, "Unknown user") || Contains(m, "Incorrect password") || Contains(m, "requires authentication"))
            return new AeroFoilException(AeroFoilErrorKind.InvalidCredentials, $"Sign-in failed: {m}");
        if (Contains(m, "Too many failed attempts"))
            return new AeroFoilException(AeroFoilErrorKind.RateLimited, m);
        if (Contains(m, "does not have access") || Contains(m, "Access blocked") || Contains(m, "restricted"))
            return Contains(m, "Tinfoil/CyberFoil")
                ? new AeroFoilException(AeroFoilErrorKind.ClientBlocked, ClientBlockedMessage)
                : new AeroFoilException(AeroFoilErrorKind.AccessDenied, m);
        if (Contains(m, "frozen"))
            return new AeroFoilException(AeroFoilErrorKind.AccountFrozen, $"Your account is frozen: {m}");
        if (Contains(m, "Incorrect URL") || Contains(m, "Hauth"))
            return new AeroFoilException(AeroFoilErrorKind.AccessDenied, $"The server refused this host name: {m}");
        return new AeroFoilException(AeroFoilErrorKind.ServerError, $"Server error: {m}");
    }

    public static AeroFoilException FromHttpException(HttpRequestException ex)
    {
        if (ex.InnerException is AuthenticationException || ex.HttpRequestError == HttpRequestError.SecureConnectionError)
            return new AeroFoilException(AeroFoilErrorKind.InsecureConnection,
                "A secure connection could not be established: the server's TLS certificate is not trusted. " +
                "Certificate validation is never disabled; the server needs a valid certificate.", ex);
        return new AeroFoilException(AeroFoilErrorKind.Network, $"Could not reach the server: {ex.Message}", ex);
    }

    public static bool IsJson(HttpResponseMessage response)
    {
        var media = response.Content.Headers.ContentType?.MediaType;
        return media is not null && (media.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
                                     media.EndsWith("+json", StringComparison.OrdinalIgnoreCase) ||
                                     media.Equals("text/json", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsHtml(HttpResponseMessage response) =>
        string.Equals(response.Content.Headers.ContentType?.MediaType, "text/html", StringComparison.OrdinalIgnoreCase);

    /// <summary>If <paramref name="root"/> is an AeroFoil error object, throws the matching exception.</summary>
    public static void ThrowIfErrorPayload(JsonElement root, string? expectedProperty)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        if (expectedProperty is not null && root.TryGetProperty(expectedProperty, out _)) return;
        if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(err.GetString()))
            throw FromErrorMessage(err.GetString()!);
    }

    public static async Task<byte[]> ReadLimitedAsync(HttpContent content, long maxBytes, CancellationToken ct)
    {
        if (content.Headers.ContentLength is { } len && len > maxBytes)
            throw new AeroFoilException(AeroFoilErrorKind.InvalidResponse, "The server response is unexpectedly large.");

        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new AeroFoilException(AeroFoilErrorKind.InvalidResponse, "The server response is unexpectedly large.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>Reads a JSON error body produced in place of a file (e.g. auth failure on a download).</summary>
    public static async Task<AeroFoilException> ReadUnexpectedJsonAsync(HttpResponseMessage response, string context, CancellationToken ct)
    {
        try
        {
            var bytes = await ReadLimitedAsync(response.Content, MaxErrorBodyBytes, ct);
            using var doc = JsonDocument.Parse(bytes);
            ThrowIfErrorPayload(doc.RootElement, null);
        }
        catch (AeroFoilException ex)
        {
            return ex;
        }
        catch (JsonException)
        {
        }
        return new AeroFoilException(AeroFoilErrorKind.InvalidResponse, $"The server returned an unexpected response instead of {context}.");
    }

    private static async Task<string?> TryReadErrorMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var bytes = await ReadLimitedAsync(response.Content, MaxErrorBodyBytes, ct);
            if (bytes.Length == 0) return null;
            if (IsJson(response) || bytes[0] == (byte)'{')
            {
                using var doc = JsonDocument.Parse(bytes);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
                    return err.GetString();
                return null;
            }
            var text = Encoding.UTF8.GetString(bytes).Trim();
            return text.Length is > 0 and < 300 && !text.StartsWith('<') ? text : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
