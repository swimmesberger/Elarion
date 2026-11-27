using System.Text;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Contacts;
using Xunit;
using static Elarion.Tests.Contacts.VCardFiles;

namespace Elarion.Tests.Contacts;

public sealed class ContactErrorTests {
    [Fact]
    public async Task AnInvalidBirthday_CostsOnlyTheBirthdayWithALocatedError() {
        var builder = new StringBuilder();
        for (var i = 1; i <= 50; i++) {
            builder.Append(i == 12
                ? Card("FN:Jane Doe", "EMAIL:jane@example.com", "BDAY:31.02.1980")
                : Card($"FN:Person {i}", "EMAIL:p@example.com", "BDAY:1980-01-01"));
        }

        var result = await ReadAsync(builder.ToString());

        result.Entries.Should().HaveCount(50);
        result.Entries.Should().OnlyContain(static entry => entry.HasValue);
        var jane = result.Entries[11];
        jane.Value!.Birthday.Should().BeNull();
        jane.Value.Emails.Should().ContainSingle();
        var error = Assert.Single(jane.Errors);
        error.Code.Should().Be(ContactErrorCodes.InvalidValue);
        error.Property.Should().Be("BDAY");
        error.ExpectedKind.Should().Be("date");
        error.Value.Should().Be("31.02.1980");
        error.ItemIndex.Should().Be(12);
        error.ItemLineNumber.Should().Be(67);
        error.LineNumber.Should().Be(71);
        error.DisplayName.Should().Be("Jane Doe");
        error.ToString().Should().Be("Contact 12 (Jane Doe), line 71: The value of BDAY is not a date.");
        result.Errors.Should().ContainSingle();
    }

    [Fact]
    public async Task InvalidPhotoBase64_DropsThePhotoWithAnError() {
        var entry = Assert.Single((await ReadAsync(Card("FN:A", "PHOTO;ENCODING=b;TYPE=JPEG:!!!not base64!!!"))).Entries);

        entry.Value!.Photo.Should().BeNull();
        var error = Assert.Single(entry.Errors);
        error.Code.Should().Be(ContactErrorCodes.InvalidValue);
        error.Property.Should().Be("PHOTO");
        error.ExpectedKind.Should().Be("photo");
    }

    [Fact]
    public async Task ACardWithNothingUsable_HasNoContactAndAnEmptyError() {
        var entry = Assert.Single((await ReadAsync(Card())).Entries);

        entry.HasValue.Should().BeFalse();
        Assert.Single(entry.Errors).Code.Should().Be(ContactErrorCodes.Empty);
        entry.Errors[0].ToString().Should().Be("Contact 1, line 1: The vCard has no formatted name, name, organization, e-mail address or phone number.");
    }

    [Fact]
    public async Task ACardWithoutFn_TakesItsDisplayNameFromTheName() {
        var contact = await ReadOneAsync(Lines("BEGIN:VCARD", "VERSION:2.1", "N:Doe;John;;;", "TEL;CELL:+1", "END:VCARD"));

        contact.FormattedName.Should().BeNull();
        contact.DisplayName.Should().Be("John Doe");
    }

    [Fact]
    public async Task DisplayName_FallsBackToOrganizationEmailAndPhone() {
        (await ReadOneAsync(Card("ORG:ACME"))).DisplayName.Should().Be("ACME");
        (await ReadOneAsync(Card("EMAIL:a@example.com", "TEL:1"))).DisplayName.Should().Be("a@example.com");
        (await ReadOneAsync(Card("TEL:+43 1"))).DisplayName.Should().Be("+43 1");
    }

    [Fact]
    public async Task ALastCardWithoutEnd_IsReadWithAnUnexpectedEndError() {
        var result = await ReadAsync(Card("FN:One") + Lines("BEGIN:VCARD", "VERSION:3.0", "FN:Two", "TEL:1"));

        result.Entries.Should().HaveCount(2);
        var last = result.Entries[1];
        last.Value!.FormattedName.Should().Be("Two");
        Assert.Single(last.Errors).Code.Should().Be(ContactErrorCodes.UnexpectedEnd);
    }

    [Fact]
    public async Task ACardWithoutEndBeforeTheNextBegin_IsReadWithAnUnexpectedEndError() {
        var result = await ReadAsync(Lines("BEGIN:VCARD", "VERSION:3.0", "FN:One") + Card("FN:Two"));

        result.Contacts.Select(static c => c.FormattedName).Should().Equal("One", "Two");
        Assert.Single(result.Entries[0].Errors).Code.Should().Be(ContactErrorCodes.UnexpectedEnd);
        result.Entries[1].Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task ALineWithoutAColon_IsSkippedWithAMalformedLineError() {
        var entry = Assert.Single((await ReadAsync(Card("FN:A", "this line has no colon", "TEL:1"))).Entries);

        entry.Value!.Phones.Should().ContainSingle();
        var error = Assert.Single(entry.Errors);
        error.Code.Should().Be(ContactErrorCodes.MalformedLine);
        error.LineNumber.Should().Be(4);
    }

    [Fact]
    public async Task ApplicationErrors_AreLocatedLikeThePackagesOwnAndReachTheAppError() {
        var builder = new StringBuilder();
        for (var i = 1; i <= 8; i++) builder.Append(Card($"FN:Person {i}"));
        var result = await ReadAsync(builder.ToString());

        var entry = result.Entries[6];
        var added = entry.AddError("import.duplicate", "The contact is already stored.");
        entry.AddError("import.blocked_domain", "The address is blocked.", "FN");

        entry.HasValue.Should().BeTrue();
        added.ItemIndex.Should().Be(7);
        added.ItemLineNumber.Should().Be(entry.LineNumber);
        added.DisplayName.Should().Be("Person 7");
        entry.Errors[1].LineNumber.Should().Be(entry.LineNumber + 2);
        entry.Errors[1].Value.Should().Be("Person 7");
        result.Errors.Select(static e => e.Code).Should().Equal("import.duplicate", "import.blocked_domain");
        var appError = result.ToAppError("contacts");
        Fields(appError)["contacts"].Should().Contain($"Contact 7 (Person 7), line {entry.LineNumber}: The contact is already stored.");
    }

    [Fact]
    public async Task ToAppError_KeysByTheFieldCapsAndUsesTheFormatter() {
        var builder = new StringBuilder();
        for (var i = 1; i <= 300; i++) builder.Append(Card($"FN:P{i}", "BDAY:never"));
        var result = await ReadAsync(builder.ToString());

        var error = result.ToAppError("contacts");
        var formatted = result.ToAppError(format: static e => $"Kontakt {e.ItemIndex}: {e.Code}");

        error.Kind.Should().Be(ErrorKind.Validation);
        Fields(error)["contacts"].Should().HaveCount(101);
        Fields(error)["contacts"][0].Should().Be("Contact 1 (P1), line 4: The value of BDAY is not a date.");
        Fields(error)["contacts"][^1].Should().Be("200 more problems are not listed.");
        Fields(formatted)[""][0].Should().Be("Kontakt 1: contact.invalid_value");
    }

    [Fact]
    public async Task ToAppError_WithoutErrors_Throws() {
        var result = await ReadAsync(Card("FN:A"));

        var convert = () => result.ToAppError();

        convert.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("%PDF-1.7\n%âãÏÓ\n1 0 obj\n<< /Type /Catalog >>\nendobj")]
    [InlineData("PK\u0003\u0004\u0014\0\u0006\0")]
    public async Task AnUploadWithoutAVCard_IsOneUnreadableError(string content) {
        var result = await ReadAsync(content);

        result.Entries.Should().BeEmpty();
        Assert.Single(result.FileErrors).Code.Should().Be(ContactErrorCodes.Unreadable);
        result.Errors.Should().ContainSingle();
    }

    [Fact]
    public async Task EveryError_HasACodeAndAnEnglishMessage() {
        var result = await ReadAsync(Card("FN:A", "BDAY:x", "no colon", "PHOTO;VALUE=uri:file:///etc/passwd", "FN;CHARSET=nope:B") + Card());

        result.Errors.Select(static e => e.Code).Should().BeEquivalentTo([
            ContactErrorCodes.InvalidValue, ContactErrorCodes.MalformedLine, ContactErrorCodes.InvalidValue,
            ContactErrorCodes.UnknownCharset, ContactErrorCodes.Empty,
        ]);
        result.Errors.Should().OnlyContain(static e => e.Message.Length > 0 && e.Code.StartsWith("contact.", StringComparison.Ordinal));
    }

    private static IReadOnlyDictionary<string, string[]> Fields(AppError error) =>
        error.Data.Should().BeOfType<ValidationErrorData>().Subject.FieldErrors!;
}
