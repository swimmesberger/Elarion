using System.Text;
using Elarion.Contacts;
using Xunit;

namespace Elarion.Tests.Contacts;

/// <summary>Hand-written vCard files shaped as the producers of the research note write them, and read helpers.</summary>
internal static class VCardFiles {
    public static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    /// <summary>Joins lines with CRLF, as most producers end them.</summary>
    public static string Lines(params string[] lines) => string.Join("\r\n", lines) + "\r\n";

    public static ValueTask<ContactReadResult> ReadAsync(string text, ContactReadOptions? options = null) =>
        ReadAsync(Encoding.UTF8.GetBytes(text), options);

    public static async ValueTask<ContactReadResult> ReadAsync(byte[] bytes, ContactReadOptions? options = null) {
        using var stream = new MemoryStream(bytes);
        return await new ContactReader().ReadAsync(stream, options, TestCancellation);
    }

    public static async ValueTask<Contact> ReadOneAsync(string text) {
        var result = await ReadAsync(text);
        return Assert.Single(result.Entries).Value!;
    }

    public static string Card(params string[] properties) =>
        Lines(["BEGIN:VCARD", "VERSION:3.0", .. properties, "END:VCARD"]);

    /// <summary>Apple Contacts, vCard 3.0, three contacts.</summary>
    public static readonly string Apple = Lines(
        "BEGIN:VCARD",
        "VERSION:3.0",
        "PRODID:-//Apple Inc.//macOS 14.0//EN",
        "N:Doe;John;;Mr.;",
        "FN:John Doe",
        "ORG:Example Inc.;Sales",
        "TITLE:Manager",
        "EMAIL;type=INTERNET;type=WORK;type=pref:john@example.com",
        "TEL;type=CELL;type=VOICE;type=pref:+43 660 1234567",
        "TEL;type=WORK;type=VOICE:+43 1 234567",
        "item1.ADR;type=HOME;type=pref:;;Hauptstraße 1;Wien;;1010;Österreich",
        "item1.X-ABADR:at",
        "BDAY:1980-03-22",
        "NOTE:Met at the fair\\, 2019.\\nCall back.",
        "END:VCARD",
        "BEGIN:VCARD",
        "VERSION:3.0",
        "N:Müller;Anna;;;",
        "FN:Anna Müller",
        "EMAIL;type=INTERNET;type=HOME:anna@example.org",
        "TEL;type=HOME;type=VOICE:+49 30 1234",
        "ADR;type=WORK:;;Unter den Linden 5;Berlin;;10117;Deutschland",
        "BDAY;X-APPLE-OMIT-YEAR=1604:1604-07-01",
        "NOTE:Neighbour",
        "END:VCARD",
        "BEGIN:VCARD",
        "VERSION:3.0",
        "N:;;;;",
        "FN:ACME GmbH",
        "ORG:ACME GmbH;",
        "X-ABShowAs:COMPANY",
        "TEL;type=MAIN:+43 1 999",
        "EMAIL;type=INTERNET;type=WORK:office@acme.example",
        "ADR;type=WORK:;;Ring 2;Graz;;8010;Austria",
        "BDAY:2001-01-15",
        "NOTE:Supplier",
        "END:VCARD");
}
