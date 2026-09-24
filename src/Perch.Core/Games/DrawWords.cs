namespace Perch.Games;

/// <summary>How hard a <see cref="DrawWords"/> prompt is to draw (and to guess) — drives the scoring in
/// <see cref="DrawScoring"/> and the word the drawer is offered per tier.</summary>
public enum DrawDifficulty
{
    /// <summary>Everyday, concrete, easy to sketch.</summary>
    Easy,
    /// <summary>A step up — a scene, an action, or a two-word thing.</summary>
    Medium,
    /// <summary>Abstract, tricky, or long — worth the most points.</summary>
    Hard,
}

/// <summary>
/// The word bank behind the secret "Draw with Perch" arcade toy (see <c>DrawWithPerchWindow</c> in the app
/// head): three curated tiers of common, drawable prompts. Each round the drawer is <see cref="Offer"/>ed one
/// word per tier (Easy / Medium / Hard) and picks which to draw. Kept in <c>Perch.Core</c> so it's testable
/// and head-agnostic (nothing here touches Avalonia, the filesystem or the network); the word travels with the
/// round, so — unlike a daily puzzle — the bank never needs to be mirrored server-side.
/// </summary>
public static class DrawWords
{
    /// <summary>The three prompts offered to the drawer for a round — one per difficulty.</summary>
    public readonly record struct Offer(string Easy, string Medium, string Hard)
    {
        /// <summary>The word for a chosen tier.</summary>
        public string For(DrawDifficulty d) => d switch
        {
            DrawDifficulty.Easy => Easy,
            DrawDifficulty.Medium => Medium,
            _ => Hard,
        };
    }

    /// <summary>All words in a tier, lowercase. Exposed for tests and the (rare) UI that wants to browse.</summary>
    public static IReadOnlyList<string> Words(DrawDifficulty d) => d switch
    {
        DrawDifficulty.Easy => _easy,
        DrawDifficulty.Medium => _medium,
        _ => _hard,
    };

    /// <summary>Offers one random word from each tier. Deterministic for a given <paramref name="seed"/> so a
    /// test (or a replayed round) always gets the same three prompts.</summary>
    public static Offer OfferWords(int seed) => OfferWords(new Random(seed));

    /// <summary>Offers one random word from each tier using the supplied <paramref name="rng"/>.</summary>
    public static Offer OfferWords(Random rng)
    {
        ArgumentNullException.ThrowIfNull(rng);
        return new Offer(_easy[rng.Next(_easy.Length)], _medium[rng.Next(_medium.Length)], _hard[rng.Next(_hard.Length)]);
    }

    // Easy: single, concrete, unmistakable objects a child could sketch.
    private static readonly string[] _easy =
    {
        "cat","dog","sun","tree","house","car","fish","star","apple","boat",
        "book","cup","hat","key","ball","moon","cloud","flower","heart","bird",
        "eye","hand","door","chair","clock","cake","egg","leaf","shoe","spoon",
        "sock","bell","bone","drum","kite","fork","frog","gift","lamp","nose",
        "ring","snake","spider","train","bus","duck","bee","cow","pig","owl",
    };

    // Medium: two-word things, actions, or objects with more moving parts.
    private static readonly string[] _medium =
    {
        "ice cream","snowman","rainbow","robot","castle","dragon","guitar","rocket","penguin","octopus",
        "lighthouse","windmill","umbrella","volcano","dinosaur","elephant","butterfly","mermaid","pirate ship","hot dog",
        "traffic light","birthday cake","teddy bear","fire truck","paper plane","alarm clock","light bulb","ferris wheel","tree house","sand castle",
        "shopping cart","roller skate","camp fire","french fries","cactus","jellyfish","scarecrow","telescope","waterfall","hamburger",
    };

    // Hard: abstract ideas, idioms, or long/awkward-to-draw prompts worth the most points.
    private static readonly string[] _hard =
    {
        "gravity","electricity","time travel","imagination","recycling","teamwork","evolution","democracy","photosynthesis","black hole",
        "deja vu","brain freeze","couch potato","early bird","cold feet","bucket list","piece of cake","rain check","break the ice","spill the beans",
        "roller coaster","northern lights","solar eclipse","greenhouse effect","tug of war","paper trail","food chain","wild goose chase","light year","message in a bottle",
        "under the weather","raining cats and dogs","the tip of the iceberg","a blessing in disguise","when pigs fly","once in a blue moon",
    };
}
