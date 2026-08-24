using Perch.Games;
using Xunit;

namespace Perch.Tests;

public class Connect4GameTests
{
    [Fact]
    public void FreshGameIsRedToMoveOnAnEmptyBoard()
    {
        var g = new Connect4Game();
        Assert.Equal(Connect4Disc.Red, g.Turn);
        Assert.Equal(Connect4Status.InProgress, g.Status);
        Assert.False(g.IsOver);
        Assert.Equal(0, g.MoveCount);
        for (int c = 0; c < Connect4Game.Cols; c++)
            for (int r = 0; r < Connect4Game.Rows; r++)
                Assert.Equal(Connect4Disc.None, g.CellAt(c, r));
    }

    [Fact]
    public void DropStacksAndAlternatesTurns()
    {
        var g = new Connect4Game();
        Assert.Equal(0, g.Drop(3));                         // Red lands on the bottom row
        Assert.Equal(Connect4Disc.Red, g.CellAt(3, 0));
        Assert.Equal(Connect4Disc.Yellow, g.Turn);

        Assert.Equal(1, g.Drop(3));                         // Yellow stacks on top
        Assert.Equal(Connect4Disc.Yellow, g.CellAt(3, 1));
        Assert.Equal(Connect4Disc.Red, g.Turn);
        Assert.Equal(2, g.MoveCount);
    }

    [Fact]
    public void LandingRowPredictsWhereADiscSettlesWithoutMutating()
    {
        var g = new Connect4Game();
        Assert.Equal(0, g.LandingRow(2));
        g.Drop(2);
        Assert.Equal(1, g.LandingRow(2));
        Assert.Equal(1, g.MoveCount);                       // LandingRow itself dropped nothing
    }

    [Fact]
    public void FullColumnRejectsFurtherDrops()
    {
        var g = new Connect4Game();
        // Fill column 0 with six alternating discs (2 wins are impossible in a single vertical file of 3 each).
        for (int i = 0; i < Connect4Game.Rows; i++)
            Assert.True(g.Drop(0) >= 0);
        Assert.False(g.CanDrop(0));
        Assert.Equal(-1, g.LandingRow(0));
        Assert.Equal(-1, g.Drop(0));                        // rejected, no move consumed
        Assert.Equal(Connect4Game.Rows, g.MoveCount);
    }

    [Fact]
    public void OutOfRangeColumnIsRejected()
    {
        var g = new Connect4Game();
        Assert.False(g.CanDrop(-1));
        Assert.False(g.CanDrop(Connect4Game.Cols));
        Assert.Equal(-1, g.Drop(-1));
        Assert.Equal(-1, g.Drop(99));
        Assert.Equal(0, g.MoveCount);
    }

    [Fact]
    public void VerticalFourWins()
    {
        var g = new Connect4Game();
        // Red drops col 0 four times, Yellow parks harmlessly in col 1.
        g.Drop(0); g.Drop(1);   // R@0, Y@1
        g.Drop(0); g.Drop(1);   // R@0, Y@1
        g.Drop(0); g.Drop(1);   // R@0, Y@1
        g.Drop(0);              // R@0 — fourth Red in the column
        Assert.Equal(Connect4Status.RedWon, g.Status);
        Assert.True(g.IsOver);
        Assert.Equal(4, g.WinningLine.Count);
        Assert.All(g.WinningLine, cell => Assert.Equal(0, cell.Col));
    }

    [Fact]
    public void HorizontalFourWins()
    {
        var g = new Connect4Game();
        // Red builds the bottom row across cols 0-3; Yellow stacks on col 6 out of the way.
        g.Drop(0); g.Drop(6);
        g.Drop(1); g.Drop(6);
        g.Drop(2); g.Drop(6);
        g.Drop(3);              // R completes 0,1,2,3 on the bottom row
        Assert.Equal(Connect4Status.RedWon, g.Status);
        Assert.Equal(4, g.WinningLine.Count);
        Assert.All(g.WinningLine, cell => Assert.Equal(0, cell.Row));
    }

    [Fact]
    public void DiagonalFourWins()
    {
        var g = new Connect4Game();
        // Build the classic rising diagonal for Red at (0,0),(1,1),(2,2),(3,3).
        g.Drop(0);              // R (0,0)
        g.Drop(1);              // Y (1,0)
        g.Drop(1);              // R (1,1)
        g.Drop(2);              // Y (2,0)
        g.Drop(3);              // R (3,0) — filler so col 2 can be built up
        g.Drop(2);              // Y (2,1)
        g.Drop(2);              // R (2,2)
        g.Drop(3);              // Y (3,1)
        g.Drop(6);              // R filler
        g.Drop(3);              // Y (3,2)
        g.Drop(3);              // R (3,3) — completes (0,0),(1,1),(2,2),(3,3)
        Assert.Equal(Connect4Status.RedWon, g.Status);
        Assert.Equal(4, g.WinningLine.Count);
    }

    [Fact]
    public void NoMovesAcceptedAfterAWin()
    {
        var g = new Connect4Game();
        g.Drop(0); g.Drop(1);
        g.Drop(0); g.Drop(1);
        g.Drop(0); g.Drop(1);
        g.Drop(0);              // Red wins
        Assert.True(g.IsOver);
        int before = g.MoveCount;
        Assert.Equal(-1, g.Drop(5));
        Assert.Equal(before, g.MoveCount);
    }

    [Fact]
    public void FullBoardWithNoLineIsADraw()
    {
        var g = new Connect4Game();
        // A known column order that fills all 42 cells with no four-in-a-row. Playing three identical
        // columns in a row then shifting the pattern avoids any aligned run.
        int[] order =
        {
            0,1,0,1,0,1,   // cols 0 & 1
            1,0,1,0,1,0,
            2,3,2,3,2,3,   // cols 2 & 3
            3,2,3,2,3,2,
            4,5,4,5,4,5,   // cols 4 & 5
            5,4,5,4,5,4,
            6,6,6,6,6,6,   // col 6
        };
        foreach (int c in order)
            Assert.True(g.Drop(c) >= 0, $"unexpected illegal drop into {c}");
        Assert.Equal(Connect4Game.Cols * Connect4Game.Rows, g.MoveCount);
        Assert.Equal(Connect4Status.Draw, g.Status);
        Assert.True(g.IsOver);
    }

    [Fact]
    public void WouldWinIsHypotheticalAndDoesNotMutate()
    {
        var g = new Connect4Game();
        g.Drop(0); g.Drop(1);
        g.Drop(0); g.Drop(1);
        g.Drop(0); g.Drop(1);   // Red has three stacked in col 0; a fourth there wins
        Assert.True(g.WouldWin(0, Connect4Disc.Red));
        Assert.False(g.WouldWin(5, Connect4Disc.Red));
        Assert.Equal(6, g.MoveCount);                   // WouldWin dropped nothing
        Assert.Equal(Connect4Status.InProgress, g.Status);
    }

    [Fact]
    public void ResetClearsEverything()
    {
        var g = new Connect4Game();
        g.Drop(3); g.Drop(3);
        g.Reset();
        Assert.Equal(0, g.MoveCount);
        Assert.Equal(Connect4Disc.Red, g.Turn);
        Assert.Equal(Connect4Status.InProgress, g.Status);
        Assert.Empty(g.WinningLine);
        Assert.Equal(Connect4Disc.None, g.CellAt(3, 0));
    }
}

public class Connect4AiTests
{
    [Fact]
    public void AiTakesAnImmediateWin()
    {
        var g = new Connect4Game();
        // Set up so it is Red (the AI here) to move with three stacked in col 0.
        g.Drop(0); g.Drop(1);
        g.Drop(0); g.Drop(1);
        g.Drop(0); g.Drop(1);   // Red to move, col 0 wins
        Assert.Equal(Connect4Disc.Red, g.Turn);
        Assert.Equal(0, Connect4Ai.SuggestMove(g));
    }

    [Fact]
    public void AiBlocksTheOpponentsImminentWin()
    {
        var g = new Connect4Game();
        // Yellow threatens to complete col 2; Red (to move) has no win of its own, so it must block col 2.
        g.Drop(6);              // R filler (0,-)
        g.Drop(2);              // Y
        g.Drop(6);              // R filler
        g.Drop(2);              // Y
        g.Drop(5);              // R filler
        g.Drop(2);              // Y — three stacked in col 2, threatening a win
        Assert.Equal(Connect4Disc.Red, g.Turn);
        Assert.Equal(2, Connect4Ai.SuggestMove(g));
    }

    [Fact]
    public void AiOpensInTheCentreColumn()
    {
        var g = new Connect4Game();
        Assert.Equal(3, Connect4Ai.SuggestMove(g));     // most central column on an empty board
    }

    [Fact]
    public void AiReturnsMinusOneWhenTheGameIsOver()
    {
        var g = new Connect4Game();
        g.Drop(0); g.Drop(1);
        g.Drop(0); g.Drop(1);
        g.Drop(0); g.Drop(1);
        g.Drop(0);              // Red wins
        Assert.Equal(-1, Connect4Ai.SuggestMove(g));
    }
}

public class Connect4ReplayTests
{
    [Fact]
    public void ReplayReconstructsAWonGame()
    {
        // Red vertical win in column 0 (Yellow parks in column 1).
        var g = Connect4Game.Replay(new[] { 0, 1, 0, 1, 0, 1, 0 });
        Assert.Equal(Connect4Status.RedWon, g.Status);
        Assert.Equal(7, g.MoveCount);
        Assert.Equal(4, g.WinningLine.Count);
    }

    [Fact]
    public void ReplayOfEmptyListIsAFreshGame()
    {
        var g = Connect4Game.Replay(Array.Empty<int>());
        Assert.Equal(0, g.MoveCount);
        Assert.Equal(Connect4Disc.Red, g.Turn);
    }

    [Fact]
    public void ReplayStopsApplyingAfterTheGameIsDecided()
    {
        // Seven winning moves, then stray extra columns that must be ignored (the game is already over).
        var g = Connect4Game.Replay(new[] { 0, 1, 0, 1, 0, 1, 0, 2, 3, 4 });
        Assert.Equal(Connect4Status.RedWon, g.Status);
        Assert.Equal(7, g.MoveCount);   // the trailing moves were not applied
    }

    [Fact]
    public void ReplayThrowsOnAnIllegalMove()
    {
        // Column 0 is filled by the first six moves; the seventh drop into 0 is illegal.
        var bad = new[] { 0, 0, 0, 0, 0, 0, 0 };
        Assert.Throws<ArgumentException>(() => Connect4Game.Replay(bad));
    }
}
