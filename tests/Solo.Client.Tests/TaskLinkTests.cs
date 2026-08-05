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

    // -- Retargeting: what a board transfer does to prose that names the task ----

    private static readonly TaskReferences.Reference Old =
        TaskReferences.Reference.Of("DMD", "PW26", 2);

    private static readonly TaskReferences.Reference New =
        TaskReferences.Reference.Of("DMD", "NRM", 3);

    [Fact]
    public void A_reference_to_the_moved_task_follows_it_to_its_new_serial()
    {
        var rewritten = TaskReferences.Retarget("blocked by ||DMD-PW26-2|| until Friday", Old, New);

        Assert.Equal("blocked by ||DMD-NRM-3|| until Friday", rewritten);
    }

    /// <summary>
    /// The reference is written by hand, so it turns up in whatever case and
    /// padding someone typed — and every one of those names the same task.
    /// </summary>
    [Theory]
    [InlineData("||dmd-pw26-2||")]
    [InlineData("||Dmd-Pw26-2||")]
    [InlineData("||DMD-PW26-002||")]
    public void A_reference_in_any_written_form_is_caught(string written)
    {
        Assert.Equal("||DMD-NRM-3||", TaskReferences.Retarget(written, Old, New));
    }

    /// <summary>
    /// This edits prose someone else authored, so everything that isn't a
    /// reference to the moved task has to come back byte-for-byte — including
    /// other references, which keep the case they were written in.
    /// </summary>
    [Theory]
    [InlineData("see ||DMD-PW26-3|| — different task")]
    [InlineData("see ||DMD-NRM-2|| — different board")]
    [InlineData("see ||OTHER-PW26-2|| — different project")]
    [InlineData("see ||dmd-pw26-9|| — untouched, lower case and all")]
    [InlineData("not a reference: |DMD-PW26-2| or ||DMD-PW26||")]
    [InlineData("plain prose with no references at all")]
    [InlineData("")]
    public void Everything_that_is_not_the_moved_task_is_left_exactly_as_written(string markdown)
    {
        Assert.Equal(markdown, TaskReferences.Retarget(markdown, Old, New));
    }

    [Fact]
    public void Every_reference_to_the_moved_task_is_rewritten_not_just_the_first()
    {
        var rewritten = TaskReferences.Retarget(
            "||DMD-PW26-2|| blocks ||DMD-PW26-3||, and ||DMD-PW26-2|| is done", Old, New);

        Assert.Equal("||DMD-NRM-3|| blocks ||DMD-PW26-3||, and ||DMD-NRM-3|| is done", rewritten);
    }

    /// <summary>The rewritten reference has to survive the trip back through the linker.</summary>
    [Fact]
    public void A_retargeted_reference_still_renders_as_a_link_to_the_new_board()
    {
        var rendered = SoloMarkdown.LinkifySerials(TaskReferences.Retarget("||DMD-PW26-2||", Old, New));

        Assert.Equal("[DMD-NRM-3](/projects/DMD/boards/NRM?task=3)", rendered);
    }
}
