using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Elarion.WebPush;
using Xunit;

namespace Elarion.Tests.WebPush;

public sealed class WebPushEncryptionTests {
    [Fact]
    public void Encrypt_Rfc8291AppendixA_ProducesTheSpecifiedMessage() {
        // RFC 8291 §5 / Appendix A: fixed application-server key and salt make the output deterministic.
        var asPublic = Base64Url.DecodeFromChars(
            "BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8");
        using var applicationServerKey = ECDiffieHellman.Create(new ECParameters {
            Curve = ECCurve.NamedCurves.nistP256,
            D = Base64Url.DecodeFromChars("yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw"),
            Q = new ECPoint { X = asPublic[1..33], Y = asPublic[33..65] }
        });

        var body = WebPushEncryption.Encrypt(
            "When I grow up, I want to be a watermelon"u8,
            Base64Url.DecodeFromChars(
                "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4"),
            Base64Url.DecodeFromChars("BTBZMqHH6r4Tts7J_aSIgg"),
            applicationServerKey,
            Base64Url.DecodeFromChars("DGv6ra1nlYgDCS1FRnbzlw"));

        Base64Url.EncodeToString(body).Should().Be(
            "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6Tlz"
            + "AC8wEqKK6PBru3jl7A_yl95bQpu6cVPTpK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Qulcy4a-fN");
    }

    [Fact]
    public void Encrypt_FreshKeyAndSalt_DecryptsForTheSubscriber() {
        using var subscriber = new TestPushSubscriber("https://fcm.googleapis.com/fcm/send/abc");
        var plaintext = Encoding.UTF8.GetBytes("""{"title":"Grüße","body":"👋"}""");

        var first = WebPushEncryption.Encrypt(plaintext, subscriber.PublicKey, Base64Url.DecodeFromChars(subscriber.Auth));
        var second = WebPushEncryption.Encrypt(plaintext, subscriber.PublicKey, Base64Url.DecodeFromChars(subscriber.Auth));

        subscriber.Decrypt(first).Should().Equal(plaintext);
        second.Should().NotEqual(first, "every message uses a fresh ephemeral key and salt");
    }

    [Fact]
    public void Encrypt_LargestPlaintext_FillsExactlyOneRecord() {
        using var subscriber = new TestPushSubscriber("https://fcm.googleapis.com/fcm/send/abc");
        var plaintext = new byte[WebPushEncryption.MaxPlaintextLength];

        var body = WebPushEncryption.Encrypt(plaintext, subscriber.PublicKey, Base64Url.DecodeFromChars(subscriber.Auth));

        body.Length.Should().Be(WebPushEncryption.RecordSize);
        FluentActions.Invoking(() => WebPushEncryption.Encrypt(
                new byte[WebPushEncryption.MaxPlaintextLength + 1], subscriber.PublicKey,
                Base64Url.DecodeFromChars(subscriber.Auth)))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Encrypt_PointOffTheCurve_IsRejected() {
        var offCurve = new byte[65];
        offCurve[0] = 0x04;
        offCurve[64] = 1;

        FluentActions.Invoking(() => WebPushEncryption.Encrypt("x"u8, offCurve, new byte[16]))
            .Should().Throw<CryptographicException>();
    }
}
