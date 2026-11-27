using System.Xml;

namespace Elarion.WebDav;

/// <summary>
/// An <see cref="XmlWriter"/> that drops the characters XML 1.0 forbids (C0 controls other than tab, line feed and carriage
/// return, U+FFFE, U+FFFF) from text before writing it. Calendar data, cards and display names come from applications and
/// from files people upload; one control character in them would otherwise make the writer throw halfway through a
/// <c>multistatus</c>, and the client would receive a cut-off answer (Cyrus strips them the same way).
/// </summary>
internal sealed class DavSanitizingXmlWriter(XmlWriter inner) : XmlWriter {
    public override WriteState WriteState => inner.WriteState;

    public override XmlWriterSettings? Settings => inner.Settings;

    public override string? XmlLang => inner.XmlLang;

    public override XmlSpace XmlSpace => inner.XmlSpace;

    public override void WriteString(string? text) => inner.WriteString(Clean(text));

    public override void WriteChars(char[] buffer, int index, int count) => inner.WriteString(Clean(new string(buffer, index, count)));

    public override void WriteCData(string? text) => inner.WriteCData(Clean(text));

    public override void WriteComment(string? text) => inner.WriteComment(Clean(text));

    public override void WriteRaw(char[] buffer, int index, int count) => inner.WriteRaw(buffer, index, count);

    public override void WriteRaw(string data) => inner.WriteRaw(data);

    public override void WriteBase64(byte[] buffer, int index, int count) => inner.WriteBase64(buffer, index, count);

    public override void WriteCharEntity(char ch) {
        if (IsAllowed(ch)) inner.WriteCharEntity(ch);
    }

    public override void WriteSurrogateCharEntity(char lowChar, char highChar) => inner.WriteSurrogateCharEntity(lowChar, highChar);

    public override void WriteWhitespace(string? ws) => inner.WriteWhitespace(ws);

    public override void WriteEntityRef(string name) => inner.WriteEntityRef(name);

    public override void WriteProcessingInstruction(string name, string? text) => inner.WriteProcessingInstruction(name, text);

    public override void WriteDocType(string name, string? pubid, string? sysid, string? subset) => inner.WriteDocType(name, pubid, sysid, subset);

    public override void WriteStartDocument() => inner.WriteStartDocument();

    public override void WriteStartDocument(bool standalone) => inner.WriteStartDocument(standalone);

    public override void WriteEndDocument() => inner.WriteEndDocument();

    public override void WriteStartElement(string? prefix, string localName, string? ns) => inner.WriteStartElement(prefix, localName, ns);

    public override void WriteEndElement() => inner.WriteEndElement();

    public override void WriteFullEndElement() => inner.WriteFullEndElement();

    public override void WriteStartAttribute(string? prefix, string localName, string? ns) => inner.WriteStartAttribute(prefix, localName, ns);

    public override void WriteEndAttribute() => inner.WriteEndAttribute();

    public override string? LookupPrefix(string ns) => inner.LookupPrefix(ns);

    public override void Flush() => inner.Flush();

    protected override void Dispose(bool disposing) {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }

    /// <summary><paramref name="text"/> without the characters XML 1.0 forbids; the same instance when it has none.</summary>
    internal static string? Clean(string? text) {
        if (text is null) return null;
        var first = -1;
        for (var i = 0; i < text.Length; i++)
            if (!IsAllowed(text[i])) {
                first = i;
                break;
            }

        if (first < 0) return text;
        var builder = new System.Text.StringBuilder(text.Length);
        builder.Append(text, 0, first);
        for (var i = first; i < text.Length; i++)
            if (IsAllowed(text[i]))
                builder.Append(text[i]);
        return builder.ToString();
    }

    // Surrogates pass: a well-formed pair is a valid character, and the inner writer rejects a lone one as before.
    private static bool IsAllowed(char c) => c >= 0x20 ? c is not ('￾' or '￿') : c is '\t' or '\n' or '\r';
}
