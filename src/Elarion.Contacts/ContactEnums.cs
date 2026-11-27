namespace Elarion.Contacts;

/// <summary>Whether a contact is a person or an organization (FR-025).</summary>
public enum ContactKind {
    /// <summary>A person; every contact that is not marked as an organization.</summary>
    Individual,

    /// <summary>An organization: <c>KIND:org</c> (vCard 4.0) or Apple's <c>X-ABSHOWAS:COMPANY</c> (vCard 3.0).</summary>
    Organization,
}

/// <summary>
/// The types of a phone number, e-mail address, postal address or web address (FR-021), read from <c>TYPE</c>
/// parameters, bare vCard 2.1 parameters and Apple's <c>_$!&lt;…&gt;!$_</c> labels.
/// </summary>
[Flags]
public enum ContactTypes {
    /// <summary>No type.</summary>
    None = 0,

    /// <summary><c>HOME</c>.</summary>
    Home = 1,

    /// <summary><c>WORK</c>.</summary>
    Work = 2,

    /// <summary><c>CELL</c>: a mobile phone.</summary>
    Cell = 4,

    /// <summary><c>VOICE</c>.</summary>
    Voice = 8,

    /// <summary><c>FAX</c>.</summary>
    Fax = 16,

    /// <summary><c>PAGER</c>.</summary>
    Pager = 32,

    /// <summary><c>TEXT</c>: a number that receives text messages.</summary>
    Text = 64,

    /// <summary><c>VIDEO</c>.</summary>
    Video = 128,

    /// <summary><c>MAIN</c>: the main number, as Apple labels it.</summary>
    Main = 256,
}

/// <summary>The vCard version a <see cref="ContactWriter"/> writes (FR-052). vCard 2.1 is read, never written.</summary>
public enum ContactFormatVersion {
    /// <summary>vCard 3.0 (RFC 2426), the version every importer reads; the default.</summary>
    V3_0,

    /// <summary>vCard 4.0 (RFC 6350).</summary>
    V4_0,
}
