namespace Perch.Games;

/// <summary>How a single guessed letter scored against the answer — the three Wordle tile states.</summary>
public enum WordleMark
{
    /// <summary>The letter is not in the answer (grey).</summary>
    Absent,
    /// <summary>The letter is in the answer but in a different position (yellow).</summary>
    Present,
    /// <summary>The letter is in the answer at this position (green).</summary>
    Correct,
}

/// <summary>
/// The pure, UI-free engine behind the secret daily Wordle toy (see <c>WordleWindow</c> in the app head).
/// It owns the word list (used both as the daily-answer source and the accepted-guess dictionary), the
/// deterministic date → answer rotation, the classic two-pass letter scoring with duplicate-letter
/// accounting, and the tiny string codec the overlay's settings use to persist a day's guesses. Kept in
/// <c>Perch.Core</c> so it's testable and head-agnostic; nothing here touches Avalonia or the filesystem.
/// </summary>
public static class WordleGame
{
    public const int WordLength = 5;
    public const int MaxGuesses = 6;

    /// <summary>The word list, all lowercase, five letters, unique. Serves as both the pool the daily answer
    /// rotates through and the dictionary a guess must appear in to be accepted. Common words only, so the
    /// daily answer is always fair and typing an ordinary word usually validates.</summary>
    public static IReadOnlyList<string> Words => _words;

    // Fixed rotation origin. The daily answer is Words[daysSinceEpoch % Words.Count], so the puzzle is the
    // same for everyone on a given calendar day and never needs a network call. The date value itself is
    // arbitrary; keep it stable so the rotation doesn't jump.
    private static readonly DateOnly Epoch = new(2021, 6, 19);

    /// <summary>Whole days from the rotation epoch to <paramref name="day"/> (can be negative before it).</summary>
    public static int DayIndex(DateOnly day) => day.DayNumber - Epoch.DayNumber;

    /// <summary>The answer for a given calendar day — deterministic, so reopening the toy resumes the same
    /// puzzle. Modulo is normalised so dates before the epoch still map into range.</summary>
    public static string AnswerFor(DateOnly day)
    {
        int i = DayIndex(day) % _words.Length;
        if (i < 0) i += _words.Length;
        return _words[i];
    }

    /// <summary>True when <paramref name="guess"/> is a five-letter word in the accepted dictionary
    /// (case-insensitive). Mirrors Wordle's "not in word list" gate.</summary>
    public static bool IsAcceptedGuess(string? guess) =>
        guess is { Length: WordLength } && _wordSet.Contains(guess.ToLowerInvariant());

    /// <summary>Scores <paramref name="guess"/> against <paramref name="answer"/> as five tile marks, using
    /// the classic two-pass rule: exact positions go green first, then remaining letters go yellow only while
    /// an unmatched copy of that letter is still left in the answer — so a doubled guess letter can't earn
    /// more marks than the answer actually contains. Both words are compared case-insensitively.</summary>
    public static WordleMark[] Score(string guess, string answer)
    {
        guess = (guess ?? "").ToLowerInvariant();
        answer = (answer ?? "").ToLowerInvariant();
        int n = Math.Min(guess.Length, answer.Length);
        var marks = new WordleMark[n];

        // Tally the answer's letters, then spend a copy for each green, so pass two only pays yellows out of
        // what's genuinely left over.
        var remaining = new Dictionary<char, int>();
        foreach (char c in answer) remaining[c] = remaining.GetValueOrDefault(c) + 1;

        for (int i = 0; i < n; i++)
            if (guess[i] == answer[i]) { marks[i] = WordleMark.Correct; remaining[guess[i]]--; }

        for (int i = 0; i < n; i++)
        {
            if (marks[i] == WordleMark.Correct) continue;
            if (remaining.GetValueOrDefault(guess[i]) > 0) { marks[i] = WordleMark.Present; remaining[guess[i]]--; }
            else marks[i] = WordleMark.Absent;
        }
        return marks;
    }

    /// <summary>The best mark a letter has earned across every scored guess so far — Correct beats Present
    /// beats Absent. Drives the on-screen keyboard's per-key colouring. Keys are uppercase.</summary>
    public static IReadOnlyDictionary<char, WordleMark> KeyboardState(IEnumerable<string> guesses, string answer)
    {
        var best = new Dictionary<char, WordleMark>();
        foreach (string g in guesses)
        {
            var marks = Score(g, answer);
            for (int i = 0; i < marks.Length; i++)
            {
                char k = char.ToUpperInvariant(g[i]);
                if (!best.TryGetValue(k, out var cur) || marks[i] > cur) best[k] = marks[i];
            }
        }
        return best;
    }

    // ── Daily-state codec ─────────────────────────────────────────────────────
    // A day's progress is persisted as one compact string (AppSettings.WordleState) so it survives a restart
    // without a new settings shape or file: "yyyy-MM-dd|guess1,guess2,...". The date scopes the guesses to a
    // single puzzle — a stored state from an earlier day is treated as empty for today.

    /// <summary>Parses a persisted daily state. Returns the guesses only when the stored date matches
    /// <paramref name="today"/>; anything else (empty, malformed, or a past day) yields no guesses.</summary>
    public static List<string> ParseStateForToday(string? state, DateOnly today)
    {
        var guesses = new List<string>();
        if (string.IsNullOrWhiteSpace(state)) return guesses;
        int bar = state.IndexOf('|');
        if (bar < 0) return guesses;
        if (!DateOnly.TryParse(state[..bar], out var date) || date != today) return guesses;

        foreach (string part in state[(bar + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string g = part.Trim().ToLowerInvariant();
            if (g.Length == WordLength) guesses.Add(g);
        }
        return guesses;
    }

    /// <summary>Formats a day's guesses into the persisted string form.</summary>
    public static string FormatState(DateOnly day, IEnumerable<string> guesses) =>
        $"{day:yyyy-MM-dd}|{string.Join(',', guesses)}";

    private static readonly string[] _words =
    {
        "about","above","abuse","actor","acute","admit","adopt","adult","after","again",
        "agent","agree","ahead","alarm","album","alert","alike","alive","allow","alone",
        "along","alter","among","anger","angle","angry","apart","apple","apply","arena",
        "argue","arise","array","aside","asset","audio","audit","avoid","award","aware",
        "badly","baker","bases","basic","basis","beach","began","begin","begun","being",
        "below","bench","birth","black","blame","blank","blast","blind","block","blood",
        "bloom","board","boost","booth","bound","brain","brand","brass","brave","bread",
        "break","breed","brick","brief","bring","broad","broke","brown","brush","build",
        "built","bunch","burst","buyer","cable","carry","catch","cause","chain","chair",
        "chaos","charm","chart","chase","cheap","check","chest","chief","child","chose",
        "civil","claim","class","clean","clear","click","climb","clock","close","cloud",
        "coach","coast","could","count","court","cover","craft","crash","crazy","cream",
        "crime","cross","crowd","crown","crude","curve","cycle","daily","dairy","dance",
        "dated","dealt","death","debut","delay","depth","doing","doubt","dozen","draft",
        "drama","drank","drawn","dream","dress","drill","drink","drive","drove","dying",
        "eager","early","earth","eight","elite","empty","enemy","enjoy","enter","entry",
        "equal","error","event","every","exact","exist","extra","faith","false","fault",
        "fiber","field","fifth","fifty","fight","final","first","fixed","flash","fleet",
        "floor","fluid","focus","force","forth","forty","forum","found","frame","frank",
        "fraud","fresh","front","fruit","fully","funny","giant","given","glass","globe",
        "grace","grade","grand","grant","grass","great","green","gross","group","grown",
        "guard","guess","guest","guide","happy","heart","heavy","hence","horse","hotel",
        "house","human","ideal","image","index","inner","input","issue","joint","judge",
        "known","label","large","laser","later","laugh","layer","learn","lease","least",
        "leave","legal","level","light","limit","links","lives","local","logic","loose",
        "lower","lucky","lunch","lying","magic","major","maker","march","match","maybe",
        "mayor","meant","media","metal","might","minor","minus","mixed","model","money",
        "month","moral","motor","mount","mouse","mouth","movie","music","needs","never",
        "newly","night","noise","north","noted","novel","nurse","occur","ocean","offer",
        "often","order","other","ought","paint","panel","paper","party","peace","phase",
        "phone","photo","piece","pilot","pitch","place","plain","plane","plant","plate",
        "point","pound","power","press","price","pride","prime","print","prior","prize",
        "proof","proud","prove","queen","quick","quiet","quite","radio","raise","range",
        "rapid","ratio","reach","ready","refer","right","rival","river","rough","round",
        "route","royal","rural","scale","scene","scope","score","sense","serve","seven",
        "shall","shape","share","sharp","sheet","shelf","shell","shift","shirt","shock",
        "shoot","short","shown","sight","since","sixth","sixty","sized","skill","sleep",
        "slide","small","smart","smile","smoke","solid","solve","sorry","sound","south",
        "space","spare","speak","speed","spend","spent","split","spoke","sport","staff",
        "stage","stake","stand","start","state","steam","steel","stick","still","stock",
        "stone","stood","store","storm","story","strip","stuck","study","stuff","style",
        "sugar","suite","super","sweet","table","taken","taste","taxes","teach","teeth",
        "thank","theft","their","theme","there","these","thick","thing","think","third",
        "those","three","threw","throw","tight","times","tired","title","today","topic",
        "total","touch","tough","tower","track","trade","train","treat","trend","trial",
        "tried","tries","truck","truly","trust","truth","twice","under","undue","union",
        "unity","until","upper","upset","urban","usage","usual","valid","value","video",
        "virus","visit","vital","voice","waste","watch","water","wheel","where","which",
        "while","white","whole","whose","woman","women","world","worry","worse","worst",
        "worth","would","wound","write","wrong","wrote","yield","young","youth",
        // A handful of common Wordle staples the frequency list above missed, so ordinary opening guesses
        // validate.
        "crane","slate","trace","crate","brace","stare","roast","irate","adieu","saint",
        "pearl","prune","cider","hardy","mango","lemon","olive","peach","berry","toast",
        "flame","glory","honey","ivory","jelly","koala","dizzy","fizzy","jazzy","witty",
    };

    private static readonly HashSet<string> _wordSet = new(_words, StringComparer.Ordinal);
}
