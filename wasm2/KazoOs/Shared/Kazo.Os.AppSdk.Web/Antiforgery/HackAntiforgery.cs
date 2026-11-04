using System.Security.Cryptography;
using System.Text;
using Kazo.Os.AppSdk.Web.Http;

namespace Kazo.Os.AppSdk.Web.Antiforgery;

/// <summary>Names of the anti-forgery cookie, form field and header (ASP.NET's <c>AntiforgeryOptions</c>).</summary>
public sealed class HackAntiforgeryOptions
{
    /// <summary>Gets or sets the cookie that carries the visitor's secret (<c>.HackAntiforgery</c>).</summary>
    public string CookieName { get; set; } = ".HackAntiforgery";

    /// <summary>Gets or sets the form field of the request token (<c>__RequestVerificationToken</c>, as in ASP.NET).</summary>
    public string FormFieldName { get; set; } = "__RequestVerificationToken";

    /// <summary>Gets or sets the header a script may send the request token in (<c>RequestVerificationToken</c>).</summary>
    public string HeaderName { get; set; } = "RequestVerificationToken";
}

/// <summary>The tokens of one request: the value to put in a form (or header) and where it goes.</summary>
/// <param name="RequestToken">The request token.</param>
/// <param name="FormFieldName">The hidden field it goes in.</param>
/// <param name="HeaderName">The header a script may send it in instead.</param>
public sealed record HackAntiforgeryTokenSet(string RequestToken, string FormFieldName, string HeaderName);

/// <summary>
/// Anti-forgery tokens (ASP.NET's <c>IAntiforgery</c>, ADR 0076 §6, ADR 0083 §5): a page from another
/// site cannot <c>POST</c> a form to this one in the visitor's name, because it cannot read the token
/// the form must carry.
/// </summary>
/// <remarks>
/// <para>
/// Double-submit pattern with a signed request token, like ASP.NET Core: the visitor gets a random
/// 32-byte secret in an <c>HttpOnly</c>, <c>SameSite=Strict</c> cookie; each form carries a request
/// token <c>nonce || HMAC-SHA256(key, secret || nonce)</c>. Validation recomputes the HMAC from the
/// cookie of the request and compares in constant time.
/// </para>
/// <para>
/// The key is drawn when the site's process starts and lives in its memory, as ASP.NET's keys
/// without a persisted key ring: once the site restarts, a form rendered before is refused
/// (<c>400</c>) and must be reloaded.
/// </para>
/// </remarks>
public sealed class HackAntiforgery
{
    private const int SecretLength = 32;
    private const int NonceLength = 16;
    private static readonly object ItemsKey = new();
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    /// <summary>Initializes the service of a site.</summary>
    /// <param name="options">Cookie, field and header names.</param>
    public HackAntiforgery(HackAntiforgeryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
    }

    /// <summary>Gets the names in use.</summary>
    public HackAntiforgeryOptions Options { get; }

    /// <summary>
    /// Gets a request token for a form of this response, and sets the secret cookie when the visitor
    /// has none (or an invalid one). Every form of one response shares the same token.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>The token and its field and header names.</returns>
    public HackAntiforgeryTokenSet GetAndStoreTokens(HackHttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Items.TryGetValue(ItemsKey, out object? stored) && stored is HackAntiforgeryTokenSet existing)
        {
            return existing;
        }

        byte[]? secret = ReadSecret(context);
        if (secret is null)
        {
            secret = RandomNumberGenerator.GetBytes(SecretLength);
            context.Response.Cookies.Append(Options.CookieName, ToBase64Url(secret), new HackCookieOptions
            {
                Path = "/",
                HttpOnly = true,
                SameSite = "Strict"
            });
        }

        byte[] nonce = RandomNumberGenerator.GetBytes(NonceLength);
        byte[] token = [.. nonce, .. Sign(secret, nonce)];
        HackAntiforgeryTokenSet tokens = new(ToBase64Url(token), Options.FormFieldName, Options.HeaderName);
        context.Items[ItemsKey] = tokens;
        context.Response.Headers["Cache-Control"] = "no-cache, no-store";
        return tokens;
    }

    /// <summary>
    /// Gets whether the request carries a valid token: the secret cookie, and a request token signed
    /// for it in the form field or the header.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="reason">Why it is refused, for the log and the <c>400</c> page.</param>
    public bool IsRequestValid(HackHttpContext context, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(context);
        byte[]? secret = ReadSecret(context);
        if (secret is null)
        {
            reason = $"The anti-forgery cookie \"{Options.CookieName}\" is not present.";
            return false;
        }

        string? requestToken = context.Request.Headers[Options.HeaderName];
        if (string.IsNullOrEmpty(requestToken))
        {
            requestToken = context.Request.Form[Options.FormFieldName];
        }

        if (string.IsNullOrEmpty(requestToken))
        {
            reason = $"The anti-forgery token was not sent (form field \"{Options.FormFieldName}\" or header \"{Options.HeaderName}\").";
            return false;
        }

        byte[]? token = FromBase64Url(requestToken);
        if (token is null || token.Length != NonceLength + 32
            || !CryptographicOperations.FixedTimeEquals(token.AsSpan(NonceLength), Sign(secret, token.AsSpan(0, NonceLength))))
        {
            reason = "The anti-forgery token could not be validated: it was made for another visitor, or before the site restarted.";
            return false;
        }

        reason = null;
        return true;
    }

    private byte[]? ReadSecret(HackHttpContext context)
    {
        if (!context.Request.Cookies.TryGetValue(Options.CookieName, out string? cookie))
        {
            return null;
        }

        byte[]? secret = FromBase64Url(cookie);
        return secret is { Length: SecretLength } ? secret : null;
    }

    private byte[] Sign(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> nonce)
    {
        byte[] message = [.. secret, .. nonce];
        return HMACSHA256.HashData(_key, message);
    }

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[]? FromBase64Url(string text)
    {
        StringBuilder padded = new(text.Replace('-', '+').Replace('_', '/'));
        while (padded.Length % 4 != 0)
        {
            padded.Append('=');
        }

        try
        {
            return Convert.FromBase64String(padded.ToString());
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
