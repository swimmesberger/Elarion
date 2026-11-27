namespace Elarion.AppPasswords.AspNetCore;

/// <summary>The defaults of the app-password authentication scheme.</summary>
public static class AppPasswordAuthenticationDefaults {
    /// <summary>The default scheme name: <c>ElarionAppPassword</c>.</summary>
    public const string AuthenticationScheme = "ElarionAppPassword";

    /// <summary>The default display name: <c>App password</c>.</summary>
    public const string DisplayName = "App password";

    /// <summary>The default realm of the Basic challenge: <c>DAV</c>.</summary>
    public const string Realm = "DAV";
}

/// <summary>How the client presented its credential.</summary>
public enum AppPasswordCredentialKind {
    /// <summary>HTTP Basic, the way calendar apps send an app password of scope <c>dav</c>.</summary>
    Basic = 0,

    /// <summary>A feed token in the path of a feed URL, the way subscribed calendars send it.</summary>
    Feed = 1,
}
