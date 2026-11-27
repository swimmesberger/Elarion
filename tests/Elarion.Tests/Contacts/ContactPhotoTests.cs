using AwesomeAssertions;
using Elarion.Contacts;
using Xunit;
using static Elarion.Tests.Contacts.VCardFiles;

namespace Elarion.Tests.Contacts;

public sealed class ContactPhotoTests {
    public static TheoryData<string> InlinePhotoForms() {
        var b64 = Convert.ToBase64String(ContactTestImages.Png);
        return new TheoryData<string> {
            Lines("BEGIN:VCARD", "VERSION:2.1", "FN:A", "PHOTO;ENCODING=BASE64;TYPE=PNG:", "    " + b64[..16], "    " + b64[16..], "", "END:VCARD"),
            Card("FN:A", "PHOTO;ENCODING=b:" + b64),
            Card("FN:A", "PHOTO;ENCODING=b;TYPE=PNG:" + b64[..10], " " + b64[10..20], "  " + b64[20..30], "    " + b64[30..]),
            Lines("BEGIN:VCARD", "VERSION:2.1", "FN:A", "PHOTO;ENCODING=BASE64:" + b64[..12], b64[12..], "END:VCARD"),
            Lines("BEGIN:VCARD", "VERSION:4.0", "FN:A", "PHOTO:data:image/png;base64," + b64, "END:VCARD"),
        };
    }

    [Theory]
    [MemberData(nameof(InlinePhotoForms))]
    public async Task InlinePhotos_ReadAsTheirBytesAndSniffedMediaType(string text) {
        var result = await ReadAsync(text);

        result.HasErrors.Should().BeFalse();
        var photo = result.Contacts.Single().Photo!;
        photo.Data!.Value.ToArray().Should().Equal(ContactTestImages.Png);
        photo.MediaType.Should().Be("image/png");
        photo.Uri.Should().BeNull();
    }

    [Fact]
    public async Task TheBytesDecideTheMediaType_NotTheDeclaredType() {
        var contact = await ReadOneAsync(Card("FN:A", "PHOTO;ENCODING=b;TYPE=JPEG:" + Convert.ToBase64String(ContactTestImages.Png)));

        contact.Photo!.MediaType.Should().Be("image/png");
    }

    [Fact]
    public void EveryKnownFormat_IsSniffed() {
        ContactPhoto.FromData(ContactTestImages.Jpeg).MediaType.Should().Be("image/jpeg");
        ContactPhoto.FromData(ContactTestImages.Gif).MediaType.Should().Be("image/gif");
        ContactPhoto.FromData(ContactTestImages.WebP).MediaType.Should().Be("image/webp");
        ContactPhoto.FromData(ContactTestImages.Bmp).MediaType.Should().Be("image/bmp");
        var unknown = () => ContactPhoto.FromData(new byte[] { 1, 2, 3, 4 });
        unknown.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task UnknownImageBytes_DropThePhotoWithAnError() {
        var entry = Assert.Single((await ReadAsync(Card("FN:A", "PHOTO;ENCODING=b:" + Convert.ToBase64String("not an image"u8.ToArray())))).Entries);

        entry.Value!.Photo.Should().BeNull();
        var error = Assert.Single(entry.Errors);
        error.Code.Should().Be(ContactErrorCodes.InvalidValue);
        error.ExpectedKind.Should().Be("photo-format");
        error.MediaType.Should().BeNull();
    }

    [Fact]
    public async Task APhotoOverTheLimit_IsDroppedWithAPhotoTooLargeError() {
        var big = Convert.ToBase64String(ContactTestImages.JpegOf(3 * 1024 * 1024));

        var entry = Assert.Single((await ReadAsync(Card("FN:A", "TEL:1", "PHOTO;ENCODING=b;TYPE=JPEG:" + big))).Entries);

        entry.Value!.Photo.Should().BeNull();
        entry.Value.Phones.Should().ContainSingle();
        var error = Assert.Single(entry.Errors);
        error.Code.Should().Be(ContactErrorCodes.PhotoTooLarge);
        error.Limit.Should().Be(ContactReadOptions.DefaultMaxPhotoBytes);
        error.Property.Should().Be("PHOTO");
    }

    [Fact]
    public async Task APhotoJustOverTheLimit_IsRefusedBeforeDecoding() {
        var options = new ContactReadOptions { MaxPhotoBytes = 1000 };
        var text = Card("FN:A", "PHOTO;ENCODING=b:" + Convert.ToBase64String(ContactTestImages.JpegOf(1001)));

        var entry = Assert.Single((await ReadAsync(text, options)).Entries);

        Assert.Single(entry.Errors).Code.Should().Be(ContactErrorCodes.PhotoTooLarge);
        (await ReadAsync(Card("FN:A", "PHOTO;ENCODING=b:" + Convert.ToBase64String(ContactTestImages.JpegOf(1000))), options))
            .Contacts.Single().Photo!.Data!.Value.Length.Should().Be(1000);
    }

    [Fact]
    public async Task AnHttpsPhoto_ReadsAsItsAddress() {
        var contact = await ReadOneAsync(Card("FN:A", "PHOTO:https://lh3.googleusercontent.com/a/photo.jpg"));

        contact.Photo!.Uri.Should().Be(new Uri("https://lh3.googleusercontent.com/a/photo.jpg"));
        contact.Photo.Data.Should().BeNull();
    }

    [Fact]
    public async Task AFilePhoto_IsDroppedWithAnInvalidUriError() {
        var entry = Assert.Single((await ReadAsync(Card("FN:A", "PHOTO;VALUE=uri:file:///etc/passwd"))).Entries);

        entry.Value!.Photo.Should().BeNull();
        var error = Assert.Single(entry.Errors);
        error.Code.Should().Be(ContactErrorCodes.InvalidValue);
        error.ExpectedKind.Should().Be("uri");
    }

    [Fact]
    public async Task OnlyTheFirstPhoto_IsRead() {
        var b64 = Convert.ToBase64String(ContactTestImages.Png);

        var contact = await ReadOneAsync(Card("FN:A", "PHOTO;ENCODING=b:" + b64, "PHOTO:https://example.com/2.jpg"));

        contact.Photo!.MediaType.Should().Be("image/png");
        contact.AdditionalProperties.Should().Equal(new ContactProperty("PHOTO", "https://example.com/2.jpg"));
    }

    [Fact]
    public void FromUri_AcceptsOnlyWebAddresses() {
        var file = () => ContactPhoto.FromUri(new Uri("file:///etc/passwd"));

        file.Should().Throw<ArgumentException>();
        ContactPhoto.FromUri(new Uri("http://example.com/a.png")).Uri!.Scheme.Should().Be("http");
    }

    [Fact]
    public async Task APhotoOfAnotherFormat_SaysWhatTheBytesAre() {
        var pdf = Convert.ToBase64String("%PDF-1.7 rest"u8.ToArray());
        var heic = Convert.ToBase64String([0, 0, 0, 24, .. "ftypheic"u8, 0, 0, 0, 0]);
        var declared = Convert.ToBase64String("unknown bytes"u8.ToArray());

        var result = await ReadAsync(
            Card("FN:A", "PHOTO;ENCODING=b;TYPE=JPEG:" + pdf)
            + Card("FN:B", "PHOTO:data:image/heic;base64," + heic)
            + Card("FN:C", "PHOTO;ENCODING=b;TYPE=TIFF:" + declared));

        result.Entries.Select(static e => Assert.Single(e.Errors).MediaType).Should().Equal("application/pdf", "image/heic", "image/tiff");
        result.Entries.Should().OnlyContain(static e => e.Errors[0].ExpectedKind == "photo-format");
        result.Entries[0].Errors[0].Message.Should().Be("The photo (application/pdf) is not a JPEG, PNG, GIF, WebP or BMP image.");
    }

    [Fact]
    public async Task TheFirstPhotoThatReads_IsThePhoto() {
        var entry = Assert.Single((await ReadAsync(Card(
            "FN:A", "PHOTO;ENCODING=b:" + Convert.ToBase64String("%PDF-1"u8.ToArray()),
            "PHOTO;ENCODING=b:" + Convert.ToBase64String(ContactTestImages.Png),
            "PHOTO;ENCODING=b:" + Convert.ToBase64String(ContactTestImages.Gif)))).Entries);

        entry.Value!.Photo!.MediaType.Should().Be("image/png");
        Assert.Single(entry.Errors).MediaType.Should().Be("application/pdf");
        var further = Assert.Single(entry.Value.AdditionalProperties);
        ContactPhoto.TryRead(further, 1024, out var gif, out var error).Should().BeTrue();
        gif!.MediaType.Should().Be("image/gif");
        error.Should().BeNull();
    }

    [Fact]
    public void TryRead_GivesTheErrorAReadWouldGive() {
        var property = new ContactProperty(null, "PHOTO", [new ContactParameter("ENCODING", "b")], Convert.ToBase64String(ContactTestImages.JpegOf(4096)))
            { LineNumber = 9 };

        ContactPhoto.TryRead(property, 1024, out var photo, out var error).Should().BeFalse();

        photo.Should().BeNull();
        error!.Code.Should().Be(ContactErrorCodes.PhotoTooLarge);
        error.LineNumber.Should().Be(9);
    }

    [Fact]
    public void TryFromData_IsFalseForBytesOfNoImageFormat() {
        ContactPhoto.TryFromData("%PDF-1"u8.ToArray(), out var none).Should().BeFalse();
        none.Should().BeNull();
        ContactPhoto.TryFromData(ContactTestImages.Png, out var png).Should().BeTrue();
        png!.MediaType.Should().Be("image/png");
    }
}
