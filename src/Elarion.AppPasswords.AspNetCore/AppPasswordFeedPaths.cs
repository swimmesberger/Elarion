using Microsoft.AspNetCore.Http;

namespace Elarion.AppPasswords.AspNetCore;

/// <summary>
/// Reads a feed token from a feed URL's path, <c>{prefix}/feeds/{token}.ics</c> (ADR-0111). The DAV host serves its
/// feeds at this shape under its prefix; <see cref="AppPasswordAuthenticationOptions.FeedTokenReader"/> uses
/// <see cref="CreateReader"/> over <see cref="DefaultPrefix"/> unless the application sets another reader.
/// </summary>
public static class AppPasswordFeedPaths {
    /// <summary>The default DAV prefix: <c>/dav</c>.</summary>
    public const string DefaultPrefix = "/dav";

    /// <summary>The segment between the prefix and the token: <c>/feeds/</c>.</summary>
    public const string FeedsSegment = "/feeds/";

    /// <summary>The extension after the token: <c>.ics</c>.</summary>
    public const string Extension = ".ics";

    /// <summary>The path of the feed for <paramref name="token"/> under <paramref name="prefix"/>.</summary>
    public static PathString GetPath(PathString prefix, string token) {
        ArgumentException.ThrowIfNullOrEmpty(token);
        return prefix.Add(new PathString(FeedsSegment + token + Extension));
    }

    /// <summary>A reader that takes the token from <c>{prefix}/feeds/{token}.ics</c> of the request path.</summary>
    /// <param name="prefix">The DAV prefix, such as <c>/dav</c>.</param>
    public static Func<HttpRequest, string?> CreateReader(PathString prefix) {
        return request => TryReadToken(request.Path, prefix, out var token) ? token : null;
    }

    /// <summary>
    /// Reads the token from <paramref name="path"/> when it is <c>{prefix}/feeds/{token}.ics</c> and the token is 1 to
    /// <see cref="AppPasswordSecrets.MaxSecretLength"/> ASCII letters, digits, <c>-</c> or <c>_</c>.
    /// </summary>
    public static bool TryReadToken(PathString path, PathString prefix, out string token) {
        token = "";
        if (!path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase, out var rest)) return false;
        var value = rest.Value.AsSpan();
        if (!value.StartsWith(FeedsSegment, StringComparison.OrdinalIgnoreCase)
            || !value.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
            return false;
        var candidate = value[FeedsSegment.Length..^Extension.Length];
        if (candidate.IsEmpty || candidate.Length > AppPasswordSecrets.MaxSecretLength) return false;
        foreach (var c in candidate)
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))
                return false;
        token = candidate.ToString();
        return true;
    }
}
