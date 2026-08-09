namespace Korat.Domain.Tests;

public class DisplayNameRulesTests
{
    // ── IsValidDisplayName (no-control-chars variant used by Node) ──────────────

    [Theory]
    [InlineData("My Node")]
    [InlineData("a")]
    [InlineData("x")]
    public void IsValidDisplayName_Strict_AcceptsNormalNames(string name)
    {
        Assert.True(DisplayNameRules.IsValid(name, allowControlChars: false));
    }

    [Fact]
    public void IsValidDisplayName_Strict_Accepts256CharName()
    {
        var name = new string('a', 256);
        Assert.True(DisplayNameRules.IsValid(name, allowControlChars: false));
    }

    [Fact]
    public void IsValidDisplayName_Strict_Rejects257CharName()
    {
        var name = new string('a', 257);
        Assert.False(DisplayNameRules.IsValid(name, allowControlChars: false));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void IsValidDisplayName_Strict_RejectsNullOrWhitespace(string name)
    {
        Assert.False(DisplayNameRules.IsValid(name, allowControlChars: false));
    }

    [Fact]
    public void IsValidDisplayName_Strict_RejectsControlCharacters()
    {
        var nameWithTab = "My\tNode";
        Assert.False(DisplayNameRules.IsValid(nameWithTab, allowControlChars: false));
    }

    [Fact]
    public void IsValidDisplayName_Strict_RejectsNewline()
    {
        Assert.False(DisplayNameRules.IsValid("My\nNode", allowControlChars: false));
    }

    // ── IsValid (lenient variant used by McpServer and Consumer) ─────────────

    [Fact]
    public void IsValidDisplayName_Lenient_AcceptsTabCharacter()
    {
        // McpServer / Consumer do not check for control characters.
        Assert.True(DisplayNameRules.IsValid("My\tServer", allowControlChars: true));
    }

    [Fact]
    public void IsValidDisplayName_Lenient_StillRejectsOverlong()
    {
        var name = new string('a', 257);
        Assert.False(DisplayNameRules.IsValid(name, allowControlChars: true));
    }

    [Fact]
    public void IsValidDisplayName_Lenient_StillRejectsWhitespace()
    {
        Assert.False(DisplayNameRules.IsValid("   ", allowControlChars: true));
    }

    // ── ValidationMessage ────────────────────────────────────────────────────────

    [Fact]
    public void ValidationMessage_Strict_ContainsControlCharMention()
    {
        var msg = DisplayNameRules.ValidationMessage(allowControlChars: false);
        Assert.Contains("control", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidationMessage_Lenient_DoesNotMentionControlChars()
    {
        var msg = DisplayNameRules.ValidationMessage(allowControlChars: true);
        Assert.DoesNotContain("control", msg, StringComparison.OrdinalIgnoreCase);
    }

    // ── IsValidProfileDisplayName (user profile — stricter length cap) ───────────

    [Theory]
    [InlineData("Ada Lovelace")]
    [InlineData("a")]
    public void IsValidProfileDisplayName_AcceptsNormalNames(string name)
    {
        Assert.True(DisplayNameRules.IsValidProfileDisplayName(name));
    }

    [Fact]
    public void IsValidProfileDisplayName_AcceptsNameAtMaxLength()
    {
        var name = new string('a', DisplayNameRules.MaxProfileDisplayNameLength);
        Assert.True(DisplayNameRules.IsValidProfileDisplayName(name));
    }

    [Fact]
    public void IsValidProfileDisplayName_RejectsNameOverMaxLength()
    {
        var name = new string('a', DisplayNameRules.MaxProfileDisplayNameLength + 1);
        Assert.False(DisplayNameRules.IsValidProfileDisplayName(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void IsValidProfileDisplayName_RejectsBlankOrWhitespace(string name)
    {
        Assert.False(DisplayNameRules.IsValidProfileDisplayName(name));
    }

    [Fact]
    public void IsValidProfileDisplayName_RejectsNewline()
    {
        Assert.False(DisplayNameRules.IsValidProfileDisplayName("Ada\nLovelace"));
    }

    [Fact]
    public void IsValidProfileDisplayName_RejectsTab()
    {
        Assert.False(DisplayNameRules.IsValidProfileDisplayName("Ada\tLovelace"));
    }

    [Fact]
    public void IsValidProfileDisplayName_MaxProfileLengthIsSmallerThanMaxLength()
    {
        // Guard: profile cap must be strictly smaller than the infrastructure cap so
        // that a name valid for a profile is always valid for a node/server label.
        Assert.True(DisplayNameRules.MaxProfileDisplayNameLength < DisplayNameRules.MaxLength);
    }

    // ── ProfileDisplayNameValidationMessage ──────────────────────────────────────

    [Fact]
    public void ProfileDisplayNameValidationMessage_ContainsControlCharMention()
    {
        var msg = DisplayNameRules.ProfileDisplayNameValidationMessage();
        Assert.Contains("control", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProfileDisplayNameValidationMessage_ContainsProfileLengthCap()
    {
        var msg = DisplayNameRules.ProfileDisplayNameValidationMessage();
        Assert.Contains(DisplayNameRules.MaxProfileDisplayNameLength.ToString(), msg);
    }
}
