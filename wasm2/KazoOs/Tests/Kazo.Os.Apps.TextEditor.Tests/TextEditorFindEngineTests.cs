using Xunit;

namespace Kazo.Os.Apps.TextEditor.Tests;

public sealed class TextEditorFindEngineTests
{
    [Fact]
    public void FindMatches_ReturnsEveryCaseInsensitiveOccurrence()
    {
        List<int> matches = TextEditorFindEngine.FindMatches("Cat cat CATalog", "cat");

        Assert.Equal([0, 4, 8], matches);
    }

    [Fact]
    public void FindMatches_EmptyQueryOrContent_ReturnsNoMatches()
    {
        Assert.Empty(TextEditorFindEngine.FindMatches("some content", ""));
        Assert.Empty(TextEditorFindEngine.FindMatches("", "query"));
    }

    [Fact]
    public void Next_WrapsAroundToTheFirstMatch()
    {
        Assert.Equal(1, TextEditorFindEngine.Next(0, matchCount: 3));
        Assert.Equal(2, TextEditorFindEngine.Next(1, matchCount: 3));
        Assert.Equal(0, TextEditorFindEngine.Next(2, matchCount: 3));
    }

    [Fact]
    public void Previous_WrapsAroundToTheLastMatch()
    {
        Assert.Equal(1, TextEditorFindEngine.Previous(2, matchCount: 3));
        Assert.Equal(0, TextEditorFindEngine.Previous(1, matchCount: 3));
        Assert.Equal(2, TextEditorFindEngine.Previous(0, matchCount: 3));
    }

    [Fact]
    public void NextAndPrevious_NoMatches_ReturnNegativeOne()
    {
        Assert.Equal(-1, TextEditorFindEngine.Next(-1, matchCount: 0));
        Assert.Equal(-1, TextEditorFindEngine.Previous(-1, matchCount: 0));
    }

    [Fact]
    public void Next_FromNoSelection_StartsAtFirstMatch()
    {
        // Simulates cycling forward before any match has been selected yet (_findIndex == -1).
        Assert.Equal(0, TextEditorFindEngine.Next(-1, matchCount: 3));
    }

    // ──────────────── Options (ADR 0058 §3) ────────────────

    [Fact]
    public void FindMatches_MatchCase_IgnoresOtherCasings()
    {
        List<int> matches = TextEditorFindEngine.FindMatches("Cat cat CATalog", "cat", new TextEditorFindOptions(MatchCase: true));

        Assert.Equal([4], matches);
    }

    [Fact]
    public void FindMatches_WholeWord_SkipsOccurrencesInsideWords()
    {
        List<int> matches = TextEditorFindEngine.FindMatches("cat concat cat_x cat. Cat", "cat", new TextEditorFindOptions(WholeWord: true));

        Assert.Equal([0, 17, 22], matches);
    }

    [Fact]
    public void FindMatches_WholeWord_FindsOccurrenceOverlappingARejectedCandidate()
    {
        // "aa" at 0 is glued to the next 'a', but "aa" at 4 is a whole word.
        Assert.Equal([4], TextEditorFindEngine.FindMatches("aaa aa", "aa", new TextEditorFindOptions(WholeWord: true)));
    }

    [Fact]
    public void FindNextFrom_WrapsToFirstOccurrence()
    {
        Assert.Equal(3, TextEditorFindEngine.FindNextFrom("ab ab ab", "ab", 1));
        Assert.Equal(0, TextEditorFindEngine.FindNextFrom("ab ab ab", "ab", 7));
        Assert.Equal(-1, TextEditorFindEngine.FindNextFrom("ab ab ab", "zz", 0));
    }

    // ──────────────── Replace ────────────────

    [Fact]
    public void Replace_SelectionIsAMatch_ReplacesItAndSelectsTheNext()
    {
        TextEditorReplaceResult result = TextEditorFindEngine.Replace("one two one two", "one", "1", selectionStart: 0, selectionLength: 3);

        Assert.True(result.Replaced);
        Assert.Equal("1 two one two", result.Content);
        Assert.Equal(6, result.NextMatchStart);
    }

    [Fact]
    public void Replace_SelectionIsNotAMatch_OnlySelectsTheNextOccurrence()
    {
        TextEditorReplaceResult result = TextEditorFindEngine.Replace("one two one two", "one", "1", selectionStart: 2, selectionLength: 0);

        Assert.False(result.Replaced);
        Assert.Equal("one two one two", result.Content);
        Assert.Equal(8, result.NextMatchStart);
    }

    [Fact]
    public void Replace_TwoClicks_ReplaceTheNextOccurrence()
    {
        TextEditorReplaceResult first = TextEditorFindEngine.Replace("x one", "one", "1", 0, 0);
        TextEditorReplaceResult second = TextEditorFindEngine.Replace(first.Content, "one", "1", first.NextMatchStart, 3);

        Assert.False(first.Replaced);
        Assert.True(second.Replaced);
        Assert.Equal("x 1", second.Content);
        Assert.Equal(-1, second.NextMatchStart);
    }

    [Fact]
    public void Replace_ReplacementContainsQuery_DoesNotMatchInsertedTextAgain()
    {
        TextEditorReplaceResult result = TextEditorFindEngine.Replace("cat dog cat", "cat", "cats", 0, 3);

        Assert.Equal("cats dog cat", result.Content);
        Assert.Equal(9, result.NextMatchStart);
    }

    [Fact]
    public void Replace_LastOccurrence_WrapsToTheFirstRemainingOne()
    {
        TextEditorReplaceResult result = TextEditorFindEngine.Replace("cat dog cat", "cat", "cow", 8, 3);

        Assert.Equal("cat dog cow", result.Content);
        Assert.Equal(0, result.NextMatchStart);
    }

    [Fact]
    public void Replace_HonorsMatchCase()
    {
        // Selection "Cat" is not a case-sensitive match of "cat": it is skipped, not replaced.
        TextEditorReplaceResult result = TextEditorFindEngine.Replace("Cat cat", "cat", "dog", 0, 3, new TextEditorFindOptions(MatchCase: true));

        Assert.False(result.Replaced);
        Assert.Equal(4, result.NextMatchStart);
    }

    [Fact]
    public void Replace_EmptyQuery_DoesNothing()
    {
        TextEditorReplaceResult result = TextEditorFindEngine.Replace("text", "", "x", 0, 0);

        Assert.Equal(new TextEditorReplaceResult("text", false, -1), result);
    }

    // ──────────────── Replace All ────────────────

    [Fact]
    public void ReplaceAll_ReplacesEveryOccurrenceAndCountsThem()
    {
        TextEditorReplaceAllResult result = TextEditorFindEngine.ReplaceAll("Cat cat CATalog", "cat", "dog");

        Assert.Equal(3, result.Count);
        Assert.Equal("dog dog dogalog", result.Content);
    }

    [Fact]
    public void ReplaceAll_HonorsOptions()
    {
        TextEditorReplaceAllResult result = TextEditorFindEngine.ReplaceAll(
            "Cat cat CATalog cat", "cat", "dog", new TextEditorFindOptions(MatchCase: true, WholeWord: true));

        Assert.Equal(2, result.Count);
        Assert.Equal("Cat dog CATalog dog", result.Content);
    }

    [Fact]
    public void ReplaceAll_ReplacementContainsQuery_DoesNotLoop()
    {
        TextEditorReplaceAllResult result = TextEditorFindEngine.ReplaceAll("a a", "a", "aa");

        Assert.Equal(2, result.Count);
        Assert.Equal("aa aa", result.Content);
    }

    [Fact]
    public void ReplaceAll_EmptyReplacement_DeletesOccurrences()
    {
        Assert.Equal(new TextEditorReplaceAllResult("a  c", 1), TextEditorFindEngine.ReplaceAll("a b c", "b", ""));
    }

    [Fact]
    public void ReplaceAll_NoMatch_LeavesContentUntouched()
    {
        Assert.Equal(new TextEditorReplaceAllResult("abc", 0), TextEditorFindEngine.ReplaceAll("abc", "z", "y"));
    }

    [Theory]
    [InlineData(1, "1 replacement")]
    [InlineData(12, "12 replacements")]
    [InlineData(0, "0 replacements")]
    public void DescribeReplacementCount_Pluralizes(int count, string expected)
    {
        Assert.Equal(expected, TextEditorFindEngine.DescribeReplacementCount(count));
    }
}
