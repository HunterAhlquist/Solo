using Solo.Client.Components;
using Solo.Shared;

namespace Solo.Client.Tests;

public class TaskLinkTests
{
    [Fact]
    public void A_serial_round_trips_through_parse()
    {
        var parsed = Aliases.TryParseSerial("DMD-PXW26-2", out var project, out var board, out var number);

        Assert.True(parsed);
        Assert.Equal("DMD", project);
        Assert.Equal("PXW26", board);
        Assert.Equal(2, number);
    }

    [Fact]
    public void A_lowercase_serial_normalizes_to_upper()
    {
        Assert.True(Aliases.TryParseSerial("dmd-pxw26-2", out var project, out var board, out _));
        Assert.Equal("DMD", project);
        Assert.Equal("PXW26", board);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("DMD-PXW26")]
    [InlineData("DMD-PXW26-2-3")]
    [InlineData("DMD-PXW26-0")]
    [InlineData("DMD-PXW26--2")]
    [InlineData("DMD-PXW26-two")]
    [InlineData("TOOLONGALIAS-B-1")]
    [InlineData("D!D-B-1")]
    public void A_malformed_serial_is_rejected_rather_than_guessed_at(string? serial)
    {
        Assert.False(Aliases.TryParseSerial(serial, out _, out _, out _));
    }

    [Fact]
    public void A_serial_token_becomes_a_markdown_link_to_the_board_route()
    {
        var rendered = SoloMarkdown.LinkifySerials("see ||DMD-PXW26-2|| for context");

        Assert.Equal("see [DMD-PXW26-2](/projects/DMD/boards/PXW26?task=2) for context", rendered);
    }

    [Fact]
    public void A_lowercase_token_links_to_the_canonical_uppercase_route()
    {
        var rendered = SoloMarkdown.LinkifySerials("||dmd-pxw26-2||");

        Assert.Equal("[DMD-PXW26-2](/projects/DMD/boards/PXW26?task=2)", rendered);
    }

    [Theory]
    [InlineData("||DMD-PXW26||")]
    [InlineData("||DMD-PXW26-two||")]
    [InlineData("|DMD-PXW26-2|")]
    [InlineData("||TOOLONGALIAS-B-1||")]
    public void Text_that_only_looks_like_a_token_is_left_alone(string markdown)
    {
        Assert.Equal(markdown, SoloMarkdown.LinkifySerials(markdown));
    }

    [Fact]
    public void Multiple_tokens_in_one_description_all_become_links()
    {
        var rendered = SoloMarkdown.LinkifySerials("||A-B-1|| and ||A-B-2||");

        Assert.Equal("[A-B-1](/projects/A/boards/B?task=1) and [A-B-2](/projects/A/boards/B?task=2)", rendered);
    }
}
