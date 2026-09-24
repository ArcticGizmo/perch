namespace Perch.Games;

/// <summary>
/// The pure scoring rule for a solved "Draw with Perch" round (see <c>DrawWithPerchWindow</c>). Draw-Something
/// style: the guesser earns the word's base value minus a small penalty for each extra guess (floored at half),
/// and the drawer earns a flat reward — about two-thirds of the base — for a drawing that actually got guessed.
/// Kept in <c>Perch.Core</c> as the single source of the formula; the <c>submit_draw_guess</c> SQL RPC mirrors
/// it so scores stay authoritative and a tampered client can't inflate them. A round that ends in "give up"
/// scores nothing for either player, so it isn't modelled here.
/// </summary>
public static class DrawScoring
{
    /// <summary>The base point value of a word by difficulty — Easy 10, Medium 20, Hard 30.</summary>
    public static int Base(DrawDifficulty difficulty) => difficulty switch
    {
        DrawDifficulty.Easy => 10,
        DrawDifficulty.Medium => 20,
        _ => 30,
    };

    /// <summary>Points awarded for a solved round. <paramref name="attempts"/> is how many guesses the guesser
    /// made including the correct one (so 1 = solved first try). The guesser gets the base minus 2 per extra
    /// guess, never below half the base; the drawer gets a flat two-thirds of the base (rounded).</summary>
    /// <returns>(Guesser, Drawer) points, both ≥ 0.</returns>
    public static (int Guesser, int Drawer) Points(DrawDifficulty difficulty, int attempts)
    {
        int b = Base(difficulty);
        int floor = (b + 1) / 2;                              // ceil(b/2)
        int extra = Math.Max(0, attempts - 1);
        int guesser = Math.Max(floor, b - extra * 2);
        int drawer = (int)Math.Round(b * 2.0 / 3.0, MidpointRounding.AwayFromZero);
        return (guesser, drawer);
    }
}
