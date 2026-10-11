using PasswordTool.Core.Services;

namespace PasswordTool.Core.Tests;

public sealed class RecoveryCodeParserTests
{
    [Theory]
    [InlineData("1. abcd-1234\r\n2. 5678 9012\r\n- XyZ9-8765", "abcd-1234", "5678 9012", "XyZ9-8765")]
    [InlineData("abcd-1234, efgh-5678; ijkl-9012", "abcd-1234", "efgh-5678", "ijkl-9012")]
    public void Parse_normalizes_supported_lists_without_changing_code_values(string input, string first, string second, string third)
    {
        var result = RecoveryCodeParser.Parse(input);

        Assert.True(result.IsValid);
        Assert.Equal([first, second, third], result.Codes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abcd-1234")]
    [InlineData("abcd-1234\nabcd-1234")]
    [InlineData("abcd-1234\ninvalid/code")]
    public void Parse_rejects_incomplete_duplicate_or_malformed_lists(string input)
    {
        var result = RecoveryCodeParser.Parse(input);

        Assert.False(result.IsValid);
        Assert.Empty(result.Codes);
        Assert.NotEmpty(result.ErrorMessage!);
    }
}
