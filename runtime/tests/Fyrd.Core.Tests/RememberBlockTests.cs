using Fyrd.Core.Hub;

namespace Fyrd.Core.Tests;

public sealed class RememberBlockTests
{
    private static string Block(string body) => "```fyrd-remember\n" + body + "\n```";

    [Fact]
    public void Parse_ScopeAndText()
    {
        var r = RememberBlock.Parse("hi\n" + Block("scope: role\nAlways ask before X.") + "\nbye");
        Assert.Equal(new RememberDecl("role", "Always ask before X."), Assert.Single(r));
    }

    [Fact]
    public void Parse_SharedScope() =>
        Assert.Equal("shared", Assert.Single(RememberBlock.Parse(Block("scope: shared\nTeam fact."))).Scope);

    [Fact]
    public void Parse_DefaultScopeIsRole() =>
        Assert.Equal(new RememberDecl("role", "Just text."), Assert.Single(RememberBlock.Parse(Block("Just text."))));

    [Fact]
    public void Parse_EmptyText_Skipped() =>
        Assert.Empty(RememberBlock.Parse(Block("scope: shared\n   ")));

    [Fact]
    public void Parse_UnknownScope_Skipped() =>
        Assert.Empty(RememberBlock.Parse(Block("scope: global\nText")));

    [Fact]
    public void Parse_TwoBlocks_Both() =>
        Assert.Equal(2, RememberBlock.Parse(Block("one") + "\n\n" + Block("scope: shared\ntwo")).Count);

    [Fact]
    public void Parse_TextOver2000Chars_Skipped()
    {
        Assert.Empty(RememberBlock.Parse(Block(new string('a', 2001))));
        Assert.Single(RememberBlock.Parse(Block(new string('a', 2000))));
    }

    [Fact]
    public void Parse_OtherFenceIgnored() =>
        Assert.Empty(RememberBlock.Parse("```fyrd-done\n{}\n```"));
}
