using System;
using System.Linq;
using Perch.Games;
using Xunit;

namespace Perch.Tests;

public class WordleGameTests
{
    [Fact]
    public void EveryWordIsFiveLowercaseLetters()
    {
        foreach (string w in WordleGame.Words)
            Assert.True(w.Length == 5 && w.All(c => c is >= 'a' and <= 'z'),
                $"word list entry is not five lowercase letters: '{w}'");
    }

    [Fact]
    public void WordListHasNoDuplicates()
    {
        var dupes = WordleGame.Words.GroupBy(w => w).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(dupes.Count == 0, $"duplicate words: {string.Join(", ", dupes)}");
    }

    [Fact]
    public void DailyAnswerIsDeterministicAndAlwaysGuessable()
    {
        // Same day → same answer; and the answer must itself be an accepted guess, or the puzzle is unwinnable.
        for (int d = 0; d < 400; d++)
        {
            var day = new DateOnly(2026, 1, 1).AddDays(d);
            string a1 = WordleGame.AnswerFor(day);
            string a2 = WordleGame.AnswerFor(day);
            Assert.Equal(a1, a2);
            Assert.True(WordleGame.IsAcceptedGuess(a1), $"daily answer '{a1}' is not an accepted guess");
        }
    }

    [Fact]
    public void DailyAnswerAdvancesEachDay()
    {
        var day = new DateOnly(2026, 8, 20);
        Assert.NotEqual(WordleGame.AnswerFor(day), WordleGame.AnswerFor(day.AddDays(1)));
    }

    [Fact]
    public void AcceptedGuessIsCaseInsensitiveAndRejectsGarbage()
    {
        Assert.True(WordleGame.IsAcceptedGuess("CRANE"));
        Assert.True(WordleGame.IsAcceptedGuess("crane"));
        Assert.False(WordleGame.IsAcceptedGuess("zzzzz"));
        Assert.False(WordleGame.IsAcceptedGuess("cat"));
        Assert.False(WordleGame.IsAcceptedGuess("longer"));
        Assert.False(WordleGame.IsAcceptedGuess(null));
    }

    [Fact]
    public void ScoreMarksAllCorrectForExactMatch()
    {
        var marks = WordleGame.Score("crane", "crane");
        Assert.All(marks, m => Assert.Equal(WordleMark.Correct, m));
    }

    [Fact]
    public void ScoreMixesGreenYellowGrey()
    {
        // answer TRACE (T R A C E), guess CRATE (C R A T E):
        //   C(0) present, R(1) correct, A(2) correct, T(3) present, E(4) correct
        var marks = WordleGame.Score("crate", "trace");
        Assert.Equal(WordleMark.Present, marks[0]);
        Assert.Equal(WordleMark.Correct, marks[1]);
        Assert.Equal(WordleMark.Correct, marks[2]);
        Assert.Equal(WordleMark.Present, marks[3]);
        Assert.Equal(WordleMark.Correct, marks[4]);
    }

    [Fact]
    public void ScoreDoesNotOverCreditDoubledGuessLetters()
    {
        // answer ABBEY (A B B E Y) has two B's; guess BOBBY (B O B B Y) has three. The exact-position B
        // (index 2) claims a green first, leaving one B to yellow (index 0) and one to grey (index 3). The
        // trailing Y lands green in both words.
        var marks = WordleGame.Score("bobby", "abbey");
        Assert.Equal(WordleMark.Present, marks[0]);   // B — a copy remains after the green
        Assert.Equal(WordleMark.Absent, marks[1]);    // O — not in answer
        Assert.Equal(WordleMark.Correct, marks[2]);   // B — exact position
        Assert.Equal(WordleMark.Absent, marks[3]);    // B — answer's B's are used up
        Assert.Equal(WordleMark.Correct, marks[4]);   // Y — exact position (both end in Y)
    }

    [Fact]
    public void ScoreSingleAnswerLetterYellowsOnlyOnce()
    {
        // answer ALERT (A L E R T) has one L and one A; guess LLAMA (L L A M A). The L at index 1 matches
        // exactly (green) and spends ALERT's only L, so the leading L greys out. The A at index 2 yellows,
        // spending ALERT's only A, so the trailing A greys out too.
        var marks = WordleGame.Score("llama", "alert");
        Assert.Equal(WordleMark.Absent, marks[0]);    // L — ALERT's L is claimed by the green at index 1
        Assert.Equal(WordleMark.Correct, marks[1]);   // L — exact position
        Assert.Equal(WordleMark.Present, marks[2]);   // A — present
        Assert.Equal(WordleMark.Absent, marks[3]);    // M — absent
        Assert.Equal(WordleMark.Absent, marks[4]);    // A — ALERT's only A is used by index 2
    }

    [Fact]
    public void KeyboardStateKeepsBestMarkPerLetter()
    {
        // answer CRANE. Guess CRUSH marks C,R correct; then guess ARENA — A present etc. The 'C' stays green.
        var kb = WordleGame.KeyboardState(new[] { "crush", "arena" }, "crane");
        Assert.Equal(WordleMark.Correct, kb['C']);
        Assert.Equal(WordleMark.Correct, kb['R']);
    }

    [Fact]
    public void StateRoundTripsForToday()
    {
        var today = new DateOnly(2026, 8, 20);
        string s = WordleGame.FormatState(today, new[] { "crane", "slate" });
        var back = WordleGame.ParseStateForToday(s, today);
        Assert.Equal(new[] { "crane", "slate" }, back);
    }

    [Fact]
    public void StateFromAnotherDayIsIgnored()
    {
        var stored = WordleGame.FormatState(new DateOnly(2026, 8, 19), new[] { "crane" });
        Assert.Empty(WordleGame.ParseStateForToday(stored, new DateOnly(2026, 8, 20)));
    }

    [Fact]
    public void ParseStateToleratesGarbage()
    {
        Assert.Empty(WordleGame.ParseStateForToday(null, new DateOnly(2026, 8, 20)));
        Assert.Empty(WordleGame.ParseStateForToday("", new DateOnly(2026, 8, 20)));
        Assert.Empty(WordleGame.ParseStateForToday("no-bar-here", new DateOnly(2026, 8, 20)));
    }
}
