using System.Security.Cryptography;
using HackerOs.Platform.Core.Sessions;

namespace HackerOs.Platform.Core.Tests.Sessions;

/// <summary>ADR 0168 section 1: WebAuthn's DER ECDSA signatures become the <c>r‖s</c> form Web Crypto verifies.</summary>
public sealed class PasskeySignatureFormatTests
{
    [Fact]
    public void A_der_signature_converts_to_the_same_bytes_as_dotnet_p1363()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] data = "hello"u8.ToArray();

        for (int attempt = 0; attempt < 20; attempt++)
        {
            byte[] der = key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

            byte[] p1363 = PasskeySignatureFormat.DerToP1363(der)!;

            Assert.Equal(64, p1363.Length);
            Assert.True(key.VerifyData(data, p1363, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }
    }

    [Fact]
    public void Short_and_zero_padded_integers_are_left_padded_to_the_field_size()
    {
        // r = 0x00 0x80 (sign byte, then one byte), s = 0x01.
        byte[] der = [0x30, 0x07, 0x02, 0x02, 0x00, 0x80, 0x02, 0x01, 0x01];

        byte[] p1363 = PasskeySignatureFormat.DerToP1363(der)!;

        Assert.Equal(0x80, p1363[31]);
        Assert.Equal(0x01, p1363[63]);
        Assert.All(p1363[..31], value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x31, 0x00 })]
    [InlineData(new byte[] { 0x30, 0x06, 0x02, 0x01, 0x01, 0x02, 0x01, 0x01, 0x00 })]
    [InlineData(new byte[] { 0x30, 0x03, 0x02, 0x01, 0x01 })]
    public void A_malformed_signature_gives_null(byte[] der) =>
        Assert.Null(PasskeySignatureFormat.DerToP1363(der));
}
