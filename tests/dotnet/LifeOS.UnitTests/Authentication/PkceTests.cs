using ApiPkce = LifeOS.Api.Authentication.Pkce;
using AppPkce = LifeOS.App.Services.Auth.Pkce;

namespace LifeOS.UnitTests.Authentication;

public class PkceTests
{
    // RFC 7636, Appendix B.
    private const string RfcVerifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    private const string RfcChallenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

    [Fact]
    public void Api_ComputesTheRfcChallenge()
    {
        Assert.Equal(RfcChallenge, ApiPkce.ComputeS256Challenge(RfcVerifier));
        Assert.True(ApiPkce.Matches(RfcVerifier, RfcChallenge));
    }

    [Fact]
    public void App_ComputesTheRfcChallenge()
    {
        Assert.Equal(RfcChallenge, AppPkce.CreateS256Challenge(RfcVerifier));
    }

    [Fact]
    public void App_GeneratesValidDistinctVerifiersThatTheApiAccepts()
    {
        var first = AppPkce.CreateVerifier();
        var second = AppPkce.CreateVerifier();

        Assert.NotEqual(first, second);
        Assert.True(ApiPkce.IsValidVerifier(first));
        Assert.True(ApiPkce.IsValidS256Challenge(AppPkce.CreateS256Challenge(first)));
        Assert.True(ApiPkce.Matches(first, AppPkce.CreateS256Challenge(first)));
    }

    [Fact]
    public void Matches_WithWrongVerifier_IsFalse()
    {
        Assert.False(ApiPkce.Matches(AppPkce.CreateVerifier(), RfcChallenge));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("too-short-0123456789012345678901234567890")] // 42 characters
    [InlineData("contains space 0123456789012345678901234567890")]
    [InlineData("contains+plus/slash=0123456789012345678901234567")]
    public void IsValidVerifier_RejectsMalformedVerifiers(string? verifier)
    {
        Assert.False(ApiPkce.IsValidVerifier(verifier));
    }

    [Fact]
    public void IsValidVerifier_AcceptsTheRfcAlphabetAndLengths()
    {
        Assert.True(ApiPkce.IsValidVerifier(new string('a', 43)));
        Assert.True(ApiPkce.IsValidVerifier(new string('~', 128)));
        Assert.True(ApiPkce.IsValidVerifier("AZaz09-._~" + new string('x', 33)));
        Assert.False(ApiPkce.IsValidVerifier(new string('a', 129)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM=")] // padded
    [InlineData("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-c")]   // 42 characters
    [InlineData("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cMA")] // 44 characters
    [InlineData("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cN")]  // non-canonical trailing bits
    [InlineData("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw+cM")]  // not base64url
    public void IsValidS256Challenge_RejectsAnythingButCanonicalBase64UrlOf32Bytes(string? challenge)
    {
        Assert.False(ApiPkce.IsValidS256Challenge(challenge));
    }

    [Fact]
    public void IsValidS256Challenge_AcceptsTheRfcChallenge()
    {
        Assert.True(ApiPkce.IsValidS256Challenge(RfcChallenge));
    }
}
