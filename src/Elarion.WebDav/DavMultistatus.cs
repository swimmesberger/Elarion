namespace Elarion.WebDav;

/// <summary>Which properties a <c>PROPFIND</c> or a report asks for: named ones, all of them, or only their names.</summary>
public sealed class DavPropertySelection {
    private DavPropertySelection(bool allProp, bool propName, IReadOnlyList<DavXmlElement> requested) {
        IsAllProp = allProp;
        IsPropName = propName;
        Requested = requested;
    }

    /// <summary><c>allprop</c>: every property marked for it, plus the <see cref="Requested"/> ones (<c>include</c>).</summary>
    public bool IsAllProp { get; }

    /// <summary><c>propname</c>: the names of the defined properties, without values.</summary>
    public bool IsPropName { get; }

    /// <summary>The requested property elements (for <c>prop</c>, or <c>include</c> with <c>allprop</c>).</summary>
    public IReadOnlyList<DavXmlElement> Requested { get; }

    /// <summary><c>allprop</c> without <c>include</c>.</summary>
    public static DavPropertySelection AllProp { get; } = new(true, false, []);

    /// <summary><c>propname</c>.</summary>
    public static DavPropertySelection PropName { get; } = new(false, true, []);

    /// <summary>The properties a <c>D:prop</c> element names.</summary>
    public static DavPropertySelection Named(DavXmlElement? prop) => new(false, false, prop?.Children ?? []);

    /// <summary><c>allprop</c> with the properties an <c>include</c> element names.</summary>
    public static DavPropertySelection AllPropIncluding(DavXmlElement? include) => new(true, false, include?.Children ?? []);

    /// <summary>Whether the selection names <paramref name="name"/>.</summary>
    public bool Names(DavName name) => Requested.Any(element => element.Name == name);
}

/// <summary>Writes the elements of a <c>207 Multi-Status</c> body, for the dispatcher and the protocols' reports.</summary>
public static class DavMultistatus {
    /// <summary>Starts <c>&lt;D:multistatus&gt;</c> with the fixed namespace prefixes.</summary>
    public static void Start(DavXmlOutput output) {
        ArgumentNullException.ThrowIfNull(output);
        DavXml.StartElement(output.Writer, DavNames.D.Multistatus);
        DavXml.DeclarePrefixes(output.Writer);
    }

    /// <summary>Ends the <c>multistatus</c>.</summary>
    public static void End(DavXmlOutput output) => output.Writer.WriteEndElement();

    /// <summary>A <c>response</c> holding only an <c>href</c> and a status, such as a removed member's <c>404</c>.</summary>
    public static void WriteStatus(DavXmlOutput output, string href, int statusCode) {
        ArgumentNullException.ThrowIfNull(output);
        var writer = output.Writer;
        DavXml.StartElement(writer, DavNames.D.Response);
        DavXml.TextElement(writer, DavNames.D.Href, href);
        output.WriteStatus(statusCode);
        writer.WriteEndElement();
    }

    /// <summary>The properties a resource can have: the core's, then its protocol's (every protocol's for the principal).</summary>
    public static IEnumerable<DavProperty> PropertiesOf(DavResource resource) {
        ArgumentNullException.ThrowIfNull(resource);
        foreach (var property in DavCoreProperties.All)
            if (property.IsDefined is null || property.IsDefined(resource))
                yield return property;

        IEnumerable<DavProperty> extra = resource.Kind switch {
            DavResourceKind.Principal => resource.Context.Protocols.SelectMany(static p => p.PrincipalProperties),
            DavResourceKind.Collection => resource.Protocol?.CollectionProperties ?? [],
            DavResourceKind.Member => resource.Protocol?.MemberProperties ?? [],
            _ => [],
        };
        foreach (var property in extra)
            if (property.IsDefined is null || property.IsDefined(resource))
                yield return property;
    }

    /// <summary>
    /// A <c>response</c> for <paramref name="resource"/>: the found properties in a <c>200</c> propstat and, unless
    /// <paramref name="minimal"/> (RFC 7240 <c>return=minimal</c>), the unknown ones in a <c>404</c> propstat.
    /// </summary>
    public static async ValueTask WriteResourceAsync(DavXmlOutput output, DavResource resource, DavPropertySelection selection,
        bool minimal, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(selection);
        var writer = output.Writer;
        var defined = PropertiesOf(resource).ToList();

        DavXml.StartElement(writer, DavNames.D.Response);
        DavXml.TextElement(writer, DavNames.D.Href, resource.Href);
        DavXml.StartElement(writer, DavNames.D.Propstat);
        DavXml.StartElement(writer, DavNames.D.Prop);
        List<DavXmlElement>? missing = null;
        if (selection.IsPropName) {
            foreach (var property in defined) DavXml.EmptyElement(writer, property.Name);
        }
        else {
            var written = new HashSet<DavName>();
            if (selection.IsAllProp)
                foreach (var property in defined.Where(static p => p.InAllProp)) {
                    written.Add(property.Name);
                    await WritePropertyAsync(writer, property, resource, null, cancellationToken).ConfigureAwait(false);
                }

            foreach (var requested in selection.Requested) {
                if (!written.Add(requested.Name)) continue;
                var property = defined.Find(p => p.Name == requested.Name);
                if (property is null) (missing ??= []).Add(requested);
                else await WritePropertyAsync(writer, property, resource, requested, cancellationToken).ConfigureAwait(false);
            }
        }

        writer.WriteEndElement(); // prop
        output.WriteStatus(200);
        writer.WriteEndElement(); // propstat
        if (missing is not null && !minimal) {
            DavXml.StartElement(writer, DavNames.D.Propstat);
            DavXml.StartElement(writer, DavNames.D.Prop);
            foreach (var element in missing) DavXml.EmptyElement(writer, element.Name);
            writer.WriteEndElement();
            output.WriteStatus(404);
            writer.WriteEndElement();
        }

        writer.WriteEndElement(); // response
        await output.FlushIfFullAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask WritePropertyAsync(System.Xml.XmlWriter writer, DavProperty property, DavResource resource,
        DavXmlElement? requested, CancellationToken cancellationToken) {
        DavXml.StartElement(writer, property.Name);
        await property.Write(writer, resource, requested, cancellationToken).ConfigureAwait(false);
        writer.WriteEndElement();
    }
}
