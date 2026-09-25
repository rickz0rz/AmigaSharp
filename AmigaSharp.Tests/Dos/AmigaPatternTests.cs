using AmigaSharp.Runtime.Dos;

namespace AmigaSharp.Tests.Dos;

public class AmigaPatternTests
{
    [Theory]
    [InlineData("#?", "anything", true)]
    [InlineData("#?.info", "Disk.INFO", true)]
    [InlineData("#?.info", "Disk.inf", false)]
    [InlineData("?at", "cat", true)]
    [InlineData("?at", "at", false)]
    [InlineData("a#bc", "abbbc", true)]
    [InlineData("a#bc", "ac", true)]
    [InlineData("(logo|brush)#?", "brush12", true)]
    [InlineData("(logo|brush)#?", "gradient", false)]
    [InlineData("[a-c]1", "b1", true)]
    [InlineData("[a-c]1", "d1", false)]
    [InlineData("'?x", "?x", true)]
    [InlineData("'?x", "ax", false)]
    public void Pattern_MatchesAsAmigaDos(string pattern, string name, bool expected)
    {
        Assert.Equal(expected, AmigaPattern.ToRegex(pattern).IsMatch(name));
    }
}
