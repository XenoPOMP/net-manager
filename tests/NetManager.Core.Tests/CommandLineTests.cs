using NetManager.Core.Services;

namespace NetManager.Core.Tests;

public class CommandLineTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("", "\"\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\Program Files\NetManager\", "\"C:\\Program Files\\NetManager\\\\\"")]   // trailing backslash is doubled
    [InlineData(@"C:\no\spaces", @"C:\no\spaces")]
    [InlineData(@"a\""b", "\"a\\\\\\\"b\"")]   // backslashes before a quote are doubled, then the quote is escaped
    public void QuotesLikeCommandLineToArgvW(string arg, string expected) => Assert.Equal(expected, CommandLine.Quote(arg));

    [Fact]
    public void JoinSeparatesWithSpaces() =>
        Assert.Equal("--lang ru \"C:\\My Dir\\x.json\" --elevation-attempted", CommandLine.Join(["--lang", "ru", @"C:\My Dir\x.json", "--elevation-attempted"]));
}
