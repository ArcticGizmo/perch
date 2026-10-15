namespace Perch.Games;

/// <summary>How hard a <see cref="DrawWords"/> prompt is to draw (and to guess) — drives the scoring in
/// <see cref="DrawScoring"/> and the word the drawer is offered per tier.</summary>
public enum DrawDifficulty
{
    /// <summary>Everyday, concrete, easy to sketch.</summary>
    Easy,
    /// <summary>A step up — a scene, an action, or a two-word thing.</summary>
    Medium,
    /// <summary>Abstract, tricky, or long.</summary>
    Hard,
    /// <summary>Feelings, jargon and big ideas with no obvious picture — genuinely hard to draw, worth the most
    /// points. Appended last: the value is the chip index on the word-pick screen.</summary>
    ExtraHard,
}

/// <summary>Display helpers for <see cref="DrawDifficulty"/>.</summary>
public static class DrawDifficultyExtensions
{
    /// <summary>The tier's label as shown on the word-pick chips ("Extra hard", not "ExtraHard").</summary>
    public static string Label(this DrawDifficulty d) => d switch
    {
        DrawDifficulty.Easy => "Easy",
        DrawDifficulty.Medium => "Medium",
        DrawDifficulty.Hard => "Hard",
        _ => "Extra hard",
    };
}

/// <summary>
/// The word bank behind the secret "Draw with Perch" arcade toy (see <c>DrawWithPerchWindow</c> in the app
/// head): four curated tiers of prompts. Each round the drawer is <see cref="Offer"/>ed one word per tier
/// (Easy / Medium / Hard / Extra hard) and picks which to draw. Kept in <c>Perch.Core</c> so it's testable
/// and head-agnostic (nothing here touches Avalonia, the filesystem or the network); the word travels with the
/// round, so — unlike a daily puzzle — the bank never needs to be mirrored server-side.
/// </summary>
public static class DrawWords
{
    /// <summary>The four prompts offered to the drawer for a round — one per difficulty.</summary>
    public readonly record struct Offer(string Easy, string Medium, string Hard, string ExtraHard)
    {
        /// <summary>The word for a chosen tier.</summary>
        public string For(DrawDifficulty d) => d switch
        {
            DrawDifficulty.Easy => Easy,
            DrawDifficulty.Medium => Medium,
            DrawDifficulty.Hard => Hard,
            _ => ExtraHard,
        };
    }

    /// <summary>Every tier, in chip order.</summary>
    public static IReadOnlyList<DrawDifficulty> Tiers { get; } =
        [DrawDifficulty.Easy, DrawDifficulty.Medium, DrawDifficulty.Hard, DrawDifficulty.ExtraHard];

    /// <summary>The longest word (and letter hint) the server stores: the <c>word_length</c> / <c>hint_length</c>
    /// CHECKs on <c>draw_rounds</c> and <c>draw_requests</c>. Every word in the bank fits (pinned by a test).</summary>
    public const int MaxWordLength = 40;

    /// <summary>All words in a tier, lowercase. Exposed for tests and the (rare) UI that wants to browse.</summary>
    public static IReadOnlyList<string> Words(DrawDifficulty d) => d switch
    {
        DrawDifficulty.Easy => _easy,
        DrawDifficulty.Medium => _medium,
        DrawDifficulty.Hard => _hard,
        _ => _extraHard,
    };

    /// <summary>Offers one random word from each tier. Deterministic for a given <paramref name="seed"/> so a
    /// test (or a replayed round) always gets the same three prompts.</summary>
    public static Offer OfferWords(int seed) => OfferWords(new Random(seed));

    /// <summary>Offers one random word from each tier using the supplied <paramref name="rng"/>.</summary>
    public static Offer OfferWords(Random rng)
    {
        ArgumentNullException.ThrowIfNull(rng);
        return new Offer(Pick(_easy, rng), Pick(_medium, rng), Pick(_hard, rng), Pick(_extraHard, rng));
    }

    private static string Pick(string[] bank, Random rng) => bank[rng.Next(bank.Length)];

    // Every word is lowercase letters and single spaces only (pinned by a test), and appears in one tier only.

    // Easy: single, concrete, unmistakable objects a child could sketch.
    private static readonly string[] _easy =
    {
        "cat","dog","sun","tree","house","car","fish","star","apple","boat",
        "book","cup","hat","key","ball","moon","cloud","flower","heart","bird",
        "eye","hand","door","chair","clock","cake","egg","leaf","shoe","spoon",
        "sock","bell","bone","drum","kite","fork","frog","gift","lamp","nose",
        "ring","snake","spider","train","bus","duck","bee","cow","pig","owl",
        "banana","carrot","pizza","cookie","grapes","pear","cherry","lemon","bread","cheese",
        "mouse","rabbit","horse","sheep","bear","lion","turtle","snail","whale","crab",
        "mouth","ear","foot","tooth","smile","bed","table","window","ladder","bucket",
        "pencil","scissors","candle","glasses","crown","sword","shield","flag","balloon","bow",
        "plane","bike","ship","truck","mountain","island","rain","snow","fire","bridge",
        "hammer","saw","axe","comb","phone","bottle","box","basket","nest","feather",
    };

    // Medium: two-word things, actions, or objects with more moving parts.
    private static readonly string[] _medium =
    {
        "ice cream","snowman","rainbow","robot","castle","dragon","guitar","rocket","penguin","octopus",
        "lighthouse","windmill","umbrella","volcano","dinosaur","elephant","butterfly","mermaid","pirate ship","hot dog",
        "traffic light","birthday cake","teddy bear","fire truck","paper plane","alarm clock","light bulb","ferris wheel","tree house","sand castle",
        "shopping cart","roller skate","camp fire","french fries","cactus","jellyfish","scarecrow","telescope","waterfall","hamburger",
        "giraffe","kangaroo","flamingo","peacock","hedgehog","squirrel","crocodile","seahorse","unicorn","ghost",
        "vampire","wizard","astronaut","knight","ninja","cowboy","chef","clown","superhero","zombie",
        "helicopter","submarine","tractor","skateboard","hot air balloon","parachute","canoe","motorbike","steam train","spaceship",
        "piano","trumpet","violin","microphone","headphones","camera","television","laptop","washing machine","vacuum cleaner",
        "toothbrush","sandwich","birthday party","picnic","fishing","swimming","juggling","skiing","sleeping","sneezing",
        "beach ball","deck chair","bus stop","post box","treasure map","haunted house","bunk bed","swing set","igloo","tent",
        "spider web","bee hive","bird cage","fish tank","dog bone","pumpkin","christmas tree","snow globe","jack in the box","yo yo",
    };

    // Hard: abstract ideas, idioms, or long/awkward-to-draw prompts.
    private static readonly string[] _hard =
    {
        "gravity","electricity","time travel","imagination","recycling","teamwork","evolution","democracy","photosynthesis","black hole",
        "deja vu","brain freeze","couch potato","early bird","cold feet","bucket list","piece of cake","rain check","break the ice","spill the beans",
        "roller coaster","northern lights","solar eclipse","greenhouse effect","tug of war","paper trail","food chain","wild goose chase","light year","message in a bottle",
        "under the weather","raining cats and dogs","the tip of the iceberg","a blessing in disguise","when pigs fly","once in a blue moon",
        "hibernation","migration","erosion","magnetism","echo","reflection","shadow","static","friction","sunburn",
        "hiccups","yawn","sleepwalking","daydream","nightmare","stage fright","traffic jam","rush hour","lost luggage","puncture",
        "power cut","fire drill","snow day","heatwave","bad hair day","first day of school","retirement","wedding","graduation","job interview",
        "hot potato","tunnel vision","cloud nine","night owl","busy bee","top dog","fish out of water","elbow grease","sweet tooth","hit the sack",
        "let the cat out of the bag","a penny for your thoughts","the ball is in your court","costs an arm and a leg","barking up the wrong tree","jump on the bandwagon",
    };

    // Extra hard: feelings, jargon and big ideas with no obvious picture. The drawer has to get creative.
    private static readonly string[] _extraHard =
    {
        "nostalgia","procrastination","jealousy","sarcasm","irony","boredom","confidence","embarrassment","loneliness","curiosity",
        "awkward silence","impostor syndrome","peer pressure","small talk","inside joke","white lie","mixed feelings","guilty pleasure","midlife crisis","existential dread",
        "inflation","bureaucracy","compound interest","supply and demand","gentrification","nepotism","tax return","small print","red tape","customer service",
        "hindsight","karma","philosophy","wisdom","freedom of speech","common sense","conscience","willpower","patience","trust",
        "infinity","paradox","probability","quantum physics","the butterfly effect","reverse psychology","cognitive dissonance","survival of the fittest","natural selection","entropy",
        "jet lag","insomnia","muscle memory","short term memory","selective hearing","sleep paralysis","writers block","second wind","growing pains","sugar rush",
        "plot twist","cliffhanger","spoiler alert","fourth wall","laugh track","plot hole","sequel","hidden fees","terms and conditions","small world",
        "buffering","the cloud","machine learning","open source","technical debt","merge conflict","off by one error","dark mode","read receipt","reply all",
        "the elephant in the room","beating around the bush","the last straw","devils advocate","the benefit of the doubt","a storm in a teacup","burning the midnight oil","the calm before the storm",
    };
}
