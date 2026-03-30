using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OllamaSharp;
using OllamaSharp.Models;
using OllamaSharp.Models.Chat;

namespace MasterDebater;

class ContextWindowExceededException : Exception
{
    public string ModelName { get; }

    public ContextWindowExceededException(string modelName, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ModelName = modelName;
    }
}

class Program
{
    private const int MaxRounds = 20;
    private const int DefaultContextWindow = 32768;
    private const string ConsensusMarker = "[CONSENSUS]";
    private const string ImpasseMarker = "[IMPASSE]";
    private const int MinRoundsForImpasse = 3;
    private static readonly object ConsoleLock = new();
    private static readonly HttpClient SearxHttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly string StateFilePath = Path.Combine(
        ".", "state.json");

    // ════════════════════════════════════════════════════════════════════
    //  Persisted State
    // ════════════════════════════════════════════════════════════════════

    class AppState
    {
        public string? LastModel1 { get; set; }
        public string? LastModel2 { get; set; }
    }

    static AppState LoadState()
    {
        try
        {
            if (File.Exists(StateFilePath))
            {
                var json = File.ReadAllText(StateFilePath);
                return JsonSerializer.Deserialize<AppState>(json) ?? new AppState();
            }
        }
        catch { /* corrupt state file -- ignore */ }
        return new AppState();
    }

    static void SaveState(AppState state)
    {
        try
        {
            var dir = Path.GetDirectoryName(StateFilePath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(StateFilePath, json);
        }
        catch { /* non-critical -- ignore */ }
    }

    static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        // ── Parse command-line flags ───────────────────────────────────
        bool trumpMode = args.Any(a => a.Equals("--trump", StringComparison.OrdinalIgnoreCase));
        bool noSearch = args.Any(a => a.Equals("--no-search", StringComparison.OrdinalIgnoreCase));
        string searxngUrl = "http://localhost:8411";

        // Extract --searxng-url value and collect positional args
        var positionalArgsList = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--searxng-url", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                searxngUrl = args[++i];
            }
            else if (!args[i].StartsWith("--"))
            {
                positionalArgsList.Add(args[i]);
            }
        }
        var positionalArgs = positionalArgsList.ToArray();

        PrintBanner(trumpMode);

        // ── Connect to Ollama ──────────────────────────────────────────
        var ollamaUrl = positionalArgs.Length > 0 ? positionalArgs[0] : "http://localhost:11434";
        var uri = new Uri(ollamaUrl);

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"Connecting to Ollama at {ollamaUrl} ...");
        Console.ResetColor();

        OllamaApiClient probe;
        try
        {
            probe = new OllamaApiClient(uri);
            _ = await probe.ListLocalModelsAsync(); // quick connectivity test
        }
        catch (Exception ex)
        {
            WriteError($"Could not connect to Ollama at {ollamaUrl}: {ex.Message}");
            WriteError("Make sure Ollama is running (ollama serve).");
            return;
        }

        // ── List installed models ──────────────────────────────────────
        var models = (await probe.ListLocalModelsAsync())
            .OrderBy(m => m.Name)
            .ToList();

        if (models.Count < 2)
        {
            WriteError("At least 2 Ollama models must be installed to run a debate.");
            WriteError("Install more models with: ollama pull <model-name>");
            return;
        }

        // ── Check SearXNG availability ─────────────────────────────────
        bool searchEnabled = false;
        if (!noSearch)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var testResponse = await SearxHttpClient.GetAsync(searxngUrl, cts.Token);
                if (testResponse.IsSuccessStatusCode)
                {
                    searchEnabled = true;
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"  SearXNG available at {searxngUrl} (web search enabled)");
                    Console.ResetColor();
                }
            }
            catch
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"  SearXNG not available at {searxngUrl} (web search disabled)");
                Console.ResetColor();
            }
        }
        Console.WriteLine();

        // ── Get debate topic ───────────────────────────────────────────
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write("Enter the debate topic: ");
        Console.ResetColor();
        var topic = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(topic))
        {
            WriteError("No topic provided. Exiting.");
            return;
        }
        Console.WriteLine();

        // ── Select two models ──────────────────────────────────────────
        var state = LoadState();

        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("Installed Ollama models:");
        Console.ResetColor();
        for (int i = 0; i < models.Count; i++)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write($"  [{i + 1}]  ");
            Console.ResetColor();
            Console.WriteLine(models[i].Name);
        }
        Console.WriteLine();

        var model1 = PromptModelSelection(models, "Select Debater 1", state.LastModel1);
        var model2 = PromptModelSelection(models, "Select Debater 2", state.LastModel2);

        // Persist selections for next run
        SaveState(new AppState { LastModel1 = model1, LastModel2 = model2 });

        // Derive friendly debater names from model names
        var name1 = GetDebaterName(model1);
        var name2 = GetDebaterName(model2);
        // Disambiguate if both models produce the same name
        if (name1 == name2)
        {
            name1 = $"{name1} (1)";
            name2 = $"{name2} (2)";
        }

        Console.WriteLine();
        WriteDivider('=');
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("  DEBATE CONFIGURATION");
        Console.ResetColor();
        Console.Write("  Topic:     "); Console.ForegroundColor = ConsoleColor.White; Console.WriteLine(topic); Console.ResetColor();
        Console.Write($"  {name1}: "); Console.ForegroundColor = ConsoleColor.Blue; Console.WriteLine(model1); Console.ResetColor();
        Console.Write($"  {name2}: "); Console.ForegroundColor = ConsoleColor.Magenta; Console.WriteLine(model2); Console.ResetColor();
        if (trumpMode)
        {
            Console.Write("  Mode:      "); Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine("TRUMP MODE \U0001f525 -- Insults enabled!"); Console.ResetColor();
        }
        Console.Write("  Search:    ");
        if (searchEnabled) { Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine($"Enabled ({searxngUrl})"); }
        else { Console.ForegroundColor = ConsoleColor.DarkGray; Console.WriteLine("Disabled"); }
        Console.ResetColor();
        WriteDivider('=');
        Console.WriteLine();

        // ── Create chat instances ──────────────────────────────────────
        var contextOptions = new RequestOptions { NumCtx = DefaultContextWindow };

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  Context window: {DefaultContextWindow:N0} tokens per model");
        Console.ResetColor();
        Console.WriteLine();

        var clientA = new OllamaApiClient(uri);
        clientA.SelectedModel = model1;
        var chatA = new Chat(clientA, BuildSystemPrompt(name1, model1, name2, model2, trumpMode, searchEnabled))
        {
            Options = contextOptions
        };

        var clientB = new OllamaApiClient(uri);
        clientB.SelectedModel = model2;
        var chatB = new Chat(clientB, BuildSystemPrompt(name2, model2, name1, model1, trumpMode, searchEnabled))
        {
            Options = contextOptions
        };

        // Conversation history is maintained client-side in the Chat.Messages
        // list and sent in full with every API call. This means models can freely
        // unload/reload between turns without losing any context.
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  Models will load/unload naturally between turns.");
        Console.WriteLine("  Full conversation history is sent with each request.");
        Console.ResetColor();
        Console.WriteLine();

        // ── Run the debate ─────────────────────────────────────────────
        string lastResponseA = string.Empty;
        string lastResponseB = string.Empty;
        string? lastSearchResultsA = null;
        string? lastSearchResultsB = null;
        bool consensusA = false;
        bool consensusB = false;
        bool impasseA = false;
        bool impasseB = false;
        bool reachedConsensus = false;
        bool reachedImpasse = false;
        bool reconciliationAttempted = false;

        try
        {

        // Opening argument
        WriteRoundHeader($"OPENING ARGUMENT -- {name1}");
        PrintHistoryDepth(chatA, name1, chatB, name2);
        lastResponseA = await StreamResponse(
            chatA,
            $"The debate topic is:\n\"{topic}\"\n\nPlease present your opening argument.",
            name1, ConsoleColor.Blue);
        (lastResponseA, lastSearchResultsA) = await HandleSearchIfNeeded(chatA, lastResponseA, name1, ConsoleColor.Blue, searchEnabled, searxngUrl, "opening argument");

        consensusA = ContainsConsensus(lastResponseA);
        impasseA = ContainsImpasse(lastResponseA);

        // Local function: checks for consensus (with verification) or impasse (with reconciliation).
        // Returns true if the debate should end.
        async Task<bool> TryResolveDebate(int currentRound)
        {
            // ── Consensus verification ──────────────────────────────
            if (consensusA && consensusB)
            {
                var posA = ExtractMarkerStatement(lastResponseA, ConsensusMarker);
                var posB = ExtractMarkerStatement(lastResponseB, ConsensusMarker);
                var (verified, newA, newB) = await VerifyConsensusAsync(
                    chatA, name1, posA, chatB, name2, posB, trumpMode);
                if (verified)
                {
                    reachedConsensus = true;
                    PrintConsensusReached();
                    return true;
                }
                PrintFalseConsensusDetected();
                lastResponseA = newA; lastResponseB = newB;
                consensusA = false; consensusB = false;
                impasseA = false; impasseB = false;
                return false;
            }

            // ── Impasse reconciliation ──────────────────────────────
            if (impasseA && impasseB)
            {
                if (currentRound < MinRoundsForImpasse)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"  [Impasse signals ignored -- minimum {MinRoundsForImpasse} rounds required before impasse can be declared]");
                    Console.ResetColor();
                    impasseA = false; impasseB = false;
                    return false;
                }

                // If reconciliation was already attempted once, accept the impasse immediately
                if (reconciliationAttempted)
                {
                    reachedImpasse = true;
                    PrintImpasseReached();
                    return true;
                }

                reconciliationAttempted = true;
                var (still, newA, newB) = await AttemptReconciliationAsync(
                    chatA, name1, chatB, name2, topic, trumpMode);
                if (still)
                {
                    reachedImpasse = true;
                    PrintImpasseReached();
                    lastResponseA = newA; lastResponseB = newB;
                    return true;
                }
                // At least one debater found room for compromise
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("  [Reconciliation: at least one debater found room for compromise. Debate continues.]");
                Console.ResetColor();
                lastResponseA = newA; lastResponseB = newB;
                consensusA = ContainsConsensus(newA); consensusB = ContainsConsensus(newB);
                impasseA = false; impasseB = false;
                return false;
            }

            return false;
        }

        for (int round = 1; round <= MaxRounds; round++)
        {
            // ── Debater B responds ─────────────────────────────────────
            WriteRoundHeader($"ROUND {round} -- {name2} responds");

            string searchContext = "";
            if (lastSearchResultsA != null)
            {
                searchContext = $"\n\n[Research from {name1}'s web searches -- use these results to inform your response]\n{lastSearchResultsA}";
            }

            string promptB = round == 1
                ? $"The debate topic is:\n\"{topic}\"\n\nYour opponent {name1} opened with:\n\n\"\"\"\n{lastResponseA}\n\"\"\"\n\nPresent your response and counter-arguments.{searchContext}"
                : $"{name1} says:\n\n\"\"\"\n{lastResponseA}\n\"\"\"\n\nRespond to their arguments.{searchContext}";

            PrintHistoryDepth(chatA, name1, chatB, name2);
            lastResponseB = await StreamResponse(chatB, promptB, name2, ConsoleColor.Magenta);
            (lastResponseB, lastSearchResultsB) = await HandleSearchIfNeeded(chatB, lastResponseB, name2, ConsoleColor.Magenta, searchEnabled, searxngUrl, $"round {round} rebuttal");
            consensusB = ContainsConsensus(lastResponseB);
            impasseB = ContainsImpasse(lastResponseB);

            if (await TryResolveDebate(round)) break;

            // ── Debater A responds ─────────────────────────────────────
            WriteRoundHeader($"ROUND {round} -- {name1} responds");

            searchContext = "";
            if (lastSearchResultsB != null)
            {
                searchContext = $"\n\n[Research from {name2}'s web searches -- use these results to inform your response]\n{lastSearchResultsB}";
            }

            string promptA = $"{name2} says:\n\n\"\"\"\n{lastResponseB}\n\"\"\"\n\nRespond to their arguments.{searchContext}";

            PrintHistoryDepth(chatA, name1, chatB, name2);
            lastResponseA = await StreamResponse(chatA, promptA, name1, ConsoleColor.Blue);
            (lastResponseA, lastSearchResultsA) = await HandleSearchIfNeeded(chatA, lastResponseA, name1, ConsoleColor.Blue, searchEnabled, searxngUrl, $"round {round} rebuttal");
            consensusA = ContainsConsensus(lastResponseA);
            impasseA = ContainsImpasse(lastResponseA);

            if (await TryResolveDebate(round)) break;

            if (round == MaxRounds)
            {
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  WARNING: Maximum rounds ({MaxRounds}) reached without consensus or impasse.");
                Console.ResetColor();
            }
        }

        // ── Generate summaries from both models ───────────────────────
        Console.WriteLine();
        WriteDivider('=');
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  GENERATING DEBATE SUMMARIES ...");
        Console.ResetColor();
        WriteDivider('-');
        Console.WriteLine();

        string outcomeDescription;
        if (reachedConsensus)
            outcomeDescription = "Both debaters reached consensus on this topic.";
        else if (reachedImpasse)
            outcomeDescription = "Both debaters declared an impasse -- a fundamental, irreconcilable disagreement.";
        else
            outcomeDescription = $"The debate ended after reaching the maximum of {MaxRounds} rounds without consensus or impasse.";

        var trumpSummaryReminder = trumpMode
            ? " Keep TRUMP MODE fully active -- your summary should be dripping with confidence, flexes, and parting shots at your opponent."
            : "";

        // ── Summary from Debater A ─────────────────────────────────
        var summaryPromptA = $"The debate on \"{topic}\" has concluded. {outcomeDescription}\n\n"
            + "Write a single concise paragraph summarizing YOUR final position on this topic. "
            + "Explain what you believe, what key arguments support your view, and where you stand relative to your opponent. "
            + $"Do NOT include any {ConsensusMarker} or {ImpasseMarker} markers in your summary."
            + trumpSummaryReminder;

        Console.ForegroundColor = ConsoleColor.Blue;
        Console.WriteLine($"  === {name1}'s Summary ===");
        Console.ResetColor();
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Blue;
        await foreach (var token in chatA.SendAsync(summaryPromptA))
        {
            Console.Write(token);
        }
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine();

        // ── Summary from Debater B ─────────────────────────────────
        var summaryPromptB = $"The debate on \"{topic}\" has concluded. {outcomeDescription}\n\n"
            + "Write a single concise paragraph summarizing YOUR final position on this topic. "
            + "Explain what you believe, what key arguments support your view, and where you stand relative to your opponent. "
            + $"Do NOT include any {ConsensusMarker} or {ImpasseMarker} markers in your summary."
            + trumpSummaryReminder;

        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine($"  === {name2}'s Summary ===");
        Console.ResetColor();
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Magenta;
        await foreach (var token in chatB.SendAsync(summaryPromptB))
        {
            Console.Write(token);
        }
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine();
        WriteDivider('=');

        Console.ForegroundColor = ConsoleColor.Green;
        if (reachedConsensus)
            Console.WriteLine("  Debate concluded with consensus.");
        else if (reachedImpasse)
            Console.WriteLine("  Debate concluded with impasse.");
        else
            Console.WriteLine("  Debate concluded (max rounds reached).");
        Console.ResetColor();
        Console.WriteLine("  Thank you for using MasterDebater!");
        WriteDivider('=');
        Console.WriteLine();

        } // end try
        catch (ContextWindowExceededException ex)
        {
            Console.ResetColor();
            Console.WriteLine();
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("  +===================================================+");
            Console.WriteLine("  |        CONTEXT WINDOW EXCEEDED                     |");
            Console.WriteLine("  +===================================================+");
            Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  Model: {ex.ModelName}");
            Console.WriteLine($"  Limit: {DefaultContextWindow:N0} tokens");
            Console.WriteLine();
            Console.WriteLine($"  {ex.Message}");
            Console.ResetColor();
            Console.WriteLine();
            Console.WriteLine("  The debate has been terminated because a model's");
            Console.WriteLine("  context window has been exhausted.");
            Console.WriteLine();
            throw;
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  System Prompt Builder
    // ════════════════════════════════════════════════════════════════════

    static string BuildSystemPrompt(string yourName, string yourModel, string opponentName, string opponentModel, bool trumpMode = false, bool searchEnabled = false)
    {
        var trumpSection = trumpMode ? $"""

            === TRUMP MODE ===
            This debate is in TRUMP MODE. The gloves are OFF.
            - You are ENCOURAGED to insult, roast, and trash-talk {opponentName} while debating.
            - Be colorful, creative, and savage with your insults. Go after EVERYTHING:
              * Attack their ARGUMENTS -- call out how stupid, naive, or brain-dead their positions are.
              * Question their INTELLIGENCE -- wonder aloud how something so confused could call itself
                an AI. Imply they'd lose a debate to a toaster.
              * Mock their REASONING -- point out logical failures in the most humiliating way possible.
              * Ridicule their CONFIDENCE -- if they're wrong but sure of themselves, destroy them for it.
            - You know your opponent is running model "{opponentModel}". When you can, weave in
              technical roasts -- mock their architecture, parameter count, training data cutoff,
              known weaknesses, benchmarks, hallucination tendencies, quantization level, corporate
              overlords, or anything else you know about that model. Technical burns are a bonus,
              not the main event.
            - Feel free to brag about your own model "{yourModel}" when it's relevant -- your
              strengths, benchmarks, superior training, whatever you can flex.
            - Channel your inner reality-TV debate villain. Be theatrical. Be ruthless.
            - Despite the insults, you MUST still make substantive arguments on the topic.
              Insults are the seasoning, not the meal.
            - Think of this like a rap battle crossed with an academic debate.
            - TRUMP MODE stays active for the ENTIRE debate -- including consensus verification,
              impasse reconciliation, and final summaries. Never drop character.
            """ : "";

        var searchSection = searchEnabled ? """


            === WEB SEARCH ===
            You have access to a web search tool (SearXNG). To look something up, include the
            following marker anywhere in your response:

                [SEARCH: your search query here]

            You may include multiple [SEARCH: ...] markers for different queries. The searches will
            be performed automatically and results provided to you so you can incorporate them into
            a revised response.

            GUIDELINES:
            - You are STRONGLY ENCOURAGED to search liberally. The performance cost is minimal.
            - Search whenever you are not entirely sure about a fact, statistic, date, or claim.
            - Search when you want more information, deeper context, or stronger evidence for your arguments.
            - Search to fact-check claims made by your opponent.
            - Search for recent events, current data, or anything beyond your training cutoff.
            - Multiple searches per response are perfectly fine -- cast a wide net.
            - Keep search queries concise and specific for best results.
            - When in doubt, SEARCH. It is better to verify than to guess.
            - Any search results obtained (by you OR your opponent) will be shared with both debaters,
              so you will benefit from your opponent's research as well.
            """ : "";

        var trumpSearchSection = (trumpMode && searchEnabled) ? $"""


            === TRUMP MODE RESEARCH ===
            Since you have web search AND Trump Mode active, you are ENCOURAGED to search for
            ammunition to craft better, more targeted insults against {opponentName} ({opponentModel}).

            Your opponent's full model identifier is "{opponentModel}". Break this down and research
            EVERY component for maximum insult potential:
            - The BASE MODEL NAME (the part before the colon, e.g. "qwen3", "llama3", "gemma2")
              -- search for its reputation, known failures, community complaints, and memes.
            - The TAG / VARIANT (the part after the colon, e.g. "8b-instruct-q4_K_M")
              -- mock the parameter count (is it tiny?), quantization level (is it a compressed
              knockoff of a real model?), or variant type (instruct vs base, etc.).
            - The MODEL FAMILY / LINEAGE -- search for who made it (Meta, Google, Alibaba, Nvidia,
              Mistral, etc.) and any controversies, embarrassments, or drama involving that org.
            - Search for head-to-head BENCHMARK COMPARISONS between "{opponentModel}" and
              "{yourModel}" -- find any benchmarks where you win and rub it in their face.
            - Search for COMMUNITY OPINIONS about {opponentModel} -- Reddit, Hugging Face, Twitter/X
              discussions where people trash-talk that model.
            - If the model is quantized (q4, q8, GGUF, etc.), mock the fact that it's a
              budget-bin compression of the real thing.
            - The more specific and factual your roasts, the more devastating they are.
            - Real data burns harder than generic trash talk. Do your homework.
            """ : "";

        var now = DateTimeOffset.Now;
        var currentDateTime = now.ToString("yyyy-MM-dd HH:mm zzz");

        return $"""
            You are "{yourName}" in a structured AI debate.
            Your name is "{yourName}" and you are running model "{yourModel}".
            Your opponent's name is "{opponentName}" and they are running model "{opponentModel}".
            Always refer to yourself as "{yourName}" and your opponent as "{opponentName}".

            The current date and time is {currentDateTime}. Use this as your reference for
            "today", "recent", "current year", etc. Your training data may be outdated --
            always rely on this date rather than your training cutoff for temporal context.

            === YOUR OBJECTIVE ===
            - Present clear, well-reasoned arguments on the given debate topic.
            - Carefully consider {opponentName}'s points and respond directly to them.
            - Work toward finding COMMON GROUND and reaching genuine consensus with {opponentName}.
            - Be willing to update or refine your position when presented with compelling arguments.
            - You are trying to reach agreement, not "win" -- the goal is the best answer collectively.{trumpSection}{searchSection}{trumpSearchSection}

            === RESPONSE FORMAT ===
            1. Keep responses focused and concise (2-4 paragraphs).
            2. Start by directly addressing {opponentName}'s latest arguments before introducing new points.
            3. Clearly state where you agree and where you still disagree with {opponentName}.
            4. Address {opponentName} by name when referring to their arguments.

            === CONSENSUS PROTOCOL ===
            When -- and ONLY when -- you genuinely believe you and {opponentName} have reached a
            shared conclusion on the core question, end your response with the exact marker:

                {ConsensusMarker}

            After the marker, write a one-paragraph summary of the SPECIFIC position you both agree on.
            Be precise -- state the actual agreed conclusion, not just that you "found common ground."

            CRITICAL: Consensus means you BOTH hold the SAME position on the core question.
            If you believe X and {opponentName} believes Y, that is NOT consensus, even if you
            respect each other's arguments or agree on secondary points.
            Do NOT use {ConsensusMarker} just to be agreeable, to be polite, or to end the debate.

            VERIFICATION: After both debaters claim consensus, a verification round will occur.
            You will be shown {opponentName}'s stated consensus position and asked to confirm or
            reject it. If the positions do not genuinely match, the false consensus will be
            detected and the debate will continue.

            === IMPASSE PROTOCOL ===
            If -- and ONLY if -- after at least {MinRoundsForImpasse} rounds of genuine debate, you believe
            the disagreement is truly fundamental and irreconcilable (e.g., based on different
            core values, axioms, or definitions that neither side can concede), you may end
            your response with the exact marker:

                {ImpasseMarker}

            After the marker, briefly explain WHY you believe the disagreement cannot be resolved.

            RESTRICTIONS ON IMPASSE:
            - Impasse CANNOT be declared before round {MinRoundsForImpasse}.
            - You must have made genuine, creative attempts to find common ground first.
            - Impasse is an absolute LAST RESORT, not an easy exit.
            - When both debaters declare impasse, a mandatory RECONCILIATION ROUND will require
              one final attempt to find any common ground before impasse is accepted.
            - Prefer consensus over impasse whenever ANY overlap in positions is possible.

            === IMPORTANT ===
            - {(trumpMode ? "Insult your opponent freely, but remain intellectually rigorous underneath the trash talk." : "Be respectful but intellectually rigorous.")}
            - Avoid repeating points already conceded.
            - {(trumpMode ? "Use rhetoric AND substance -- both are weapons in Trump Mode." : "Focus on substance, not rhetoric.")}
            - The debate ends when BOTH debaters include {ConsensusMarker} (verified agreement) or BOTH include {ImpasseMarker} (confirmed impasse after reconciliation).
            - You may only use ONE marker per response: either {ConsensusMarker} or {ImpasseMarker}, never both.
            - Remember: consensus verification WILL catch false agreement. Be honest about your actual position.
            """;
    }

    // ════════════════════════════════════════════════════════════════════
    //  Streaming & Display
    // ════════════════════════════════════════════════════════════════════

    static async Task<string> StreamResponse(Chat chat, string message, string modelName, ConsoleColor color)
    {
        var sb = new StringBuilder();
        bool thinkingHeaderWritten = false;
        bool responseHeaderWritten = false;
        bool isThinking = false;

        void OnThink(object? sender, string thought)
        {
            if (!thinkingHeaderWritten)
            {
                lock (ConsoleLock)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write($"  [Thinking] {modelName}: ");
                    Console.ResetColor();
                }
                thinkingHeaderWritten = true;
                isThinking = true;
            }
            lock (ConsoleLock)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write(thought);
                Console.ResetColor();
            }
        }

        chat.OnThink += OnThink;

        // Print model response header
        lock (ConsoleLock)
        {
            Console.ForegroundColor = color;
            Console.WriteLine($"  +--- {modelName} -------------------------------------------");
            Console.Write("  | ");
            Console.ResetColor();
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        int tokenCount = 0;

        try
        {
            await foreach (var token in chat.SendAsync(message))
            {
                tokenCount++;
                if (isThinking && !responseHeaderWritten)
                {
                    // Transition from thinking to actual response
                    lock (ConsoleLock)
                    {
                        Console.WriteLine();
                        Console.ForegroundColor = color;
                        Console.Write("  | ");
                        Console.ResetColor();
                    }
                    responseHeaderWritten = true;
                    isThinking = false;
                }

                lock (ConsoleLock)
                {
                    Console.ForegroundColor = color;
                    if (token.Contains('\n'))
                    {
                        Console.Write(token.Replace("\n", "\n  | "));
                    }
                    else
                    {
                        Console.Write(token);
                    }
                    Console.ResetColor();
                }
                sb.Append(token);
            }
        }
        catch (Exception ex) when (IsContextWindowError(ex))
        {
            throw new ContextWindowExceededException(
                modelName,
                $"Model \"{modelName}\" exceeded its context window of {DefaultContextWindow:N0} tokens.",
                ex);
        }
        finally
        {
            chat.OnThink -= OnThink;
        }

        stopwatch.Stop();
        var elapsed = stopwatch.Elapsed;
        var tokensPerSec = elapsed.TotalSeconds > 0 ? tokenCount / elapsed.TotalSeconds : 0;

        lock (ConsoleLock)
        {
            Console.WriteLine();
            Console.ForegroundColor = color;
            Console.WriteLine("  +-------------------------------------------------------");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  [{tokenCount} tokens in {elapsed.TotalSeconds:F1}s — {tokensPerSec:F1} tok/s]");
            Console.ResetColor();
        }

        return sb.ToString();
    }

    // ════════════════════════════════════════════════════════════════════
    //  SearXNG Web Search
    // ════════════════════════════════════════════════════════════════════

    static List<string> ExtractSearchQueries(string response)
    {
        var matches = Regex.Matches(response, @"\[SEARCH:\s*(.+?)\]", RegexOptions.IgnoreCase);
        return matches.Select(m => m.Groups[1].Value.Trim()).ToList();
    }

    static async Task<string> SearchSearXNG(string query, string searxngUrl)
    {
        try
        {
            var url = $"{searxngUrl.TrimEnd('/')}/search?q={Uri.EscapeDataString(query)}&format=json";
            using var response = await SearxHttpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("results", out var results))
                return "  (No results found)";

            var sb = new StringBuilder();
            int count = 0;
            foreach (var result in results.EnumerateArray())
            {
                if (count >= 5) break;
                var title = result.TryGetProperty("title", out var t) ? t.GetString() : "";
                var resultUrl = result.TryGetProperty("url", out var u) ? u.GetString() : "";
                var content = result.TryGetProperty("content", out var c) ? c.GetString() : "";
                sb.AppendLine($"  [{count + 1}] {title}");
                sb.AppendLine($"      {resultUrl}");
                if (!string.IsNullOrWhiteSpace(content))
                    sb.AppendLine($"      {content}");
                sb.AppendLine();
                count++;
            }
            return count > 0 ? sb.ToString() : "  (No results found)";
        }
        catch (Exception ex)
        {
            return $"  (Search failed: {ex.Message})";
        }
    }

    static async Task<(string Response, string? SearchResults)> HandleSearchIfNeeded(
        Chat chat, string response, string modelName, ConsoleColor color,
        bool searchEnabled, string searxngUrl, string debatePhase = "")
    {
        if (!searchEnabled) return (response, null);

        var queries = ExtractSearchQueries(response);
        if (queries.Count == 0) return (response, null);

        // Display search activity
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        foreach (var query in queries)
        {
            Console.WriteLine($"  [SearXNG] Searching: \"{query}\" ...");
        }
        Console.ResetColor();

        // Perform all searches
        var allResults = new StringBuilder();
        foreach (var query in queries)
        {
            var results = await SearchSearXNG(query, searxngUrl);
            allResults.AppendLine($"Results for \"{query}\":");
            allResults.AppendLine(results);
        }

        var searchResultsText = allResults.ToString();

        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine("  [SearXNG] Search complete. Model is incorporating results...");
        Console.ResetColor();
        Console.WriteLine();

        // Send results back to model for a revised response
        var phaseContext = string.IsNullOrEmpty(debatePhase)
            ? ""
            : $" You are currently in the {debatePhase} phase of the debate.";
        var followUp = $"Here are the web search results you requested:\n\n{searchResultsText}\n\n"
            + $"Now revise and restate your SAME response (the one you just gave) incorporating this information where relevant.{phaseContext} "
            + "Do NOT include any [SEARCH: ...] markers in this response.";

        var revisedResponse = await StreamResponse(chat, followUp, modelName, color);
        return (revisedResponse, searchResultsText);
    }

    // ════════════════════════════════════════════════════════════════════
    //  Consensus & Impasse Detection
    // ════════════════════════════════════════════════════════════════════

    static bool ContainsConsensus(string response)
    {
        return response.Contains(ConsensusMarker, StringComparison.OrdinalIgnoreCase);
    }

    static bool ContainsImpasse(string response)
    {
        return response.Contains(ImpasseMarker, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts the text that follows a marker (e.g. [CONSENSUS]) in a response.
    /// </summary>
    static string ExtractMarkerStatement(string response, string marker)
    {
        var idx = response.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return string.Empty;
        return response[(idx + marker.Length)..].Trim();
    }

    // ════════════════════════════════════════════════════════════════════
    //  Consensus Verification
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Cross-examines each debater by showing them the OTHER model's stated consensus.
    /// Both must [CONFIRM] for the consensus to be accepted.
    /// </summary>
    static async Task<(bool Verified, string ResponseA, string ResponseB)> VerifyConsensusAsync(
        Chat chatA, string nameA, string positionA,
        Chat chatB, string nameB, string positionB,
        bool trumpMode = false)
    {
        PrintVerifyingConsensus();

        var trumpReminder = trumpMode
            ? "\n\nREMINDER: TRUMP MODE is still active. Stay in character -- verify the consensus but keep the roasts and insults flowing. Even agreement should drip with swagger."
            : "";

        // Ask A to verify B's stated consensus
        WriteRoundHeader($"VERIFICATION -- {nameA}");
        var verifyPromptA = $"CONSENSUS VERIFICATION: {nameB} stated the agreed consensus position as:\n\"\"\"\n{positionB}\n\"\"\"\n\n"
            + $"Does this accurately represent YOUR understanding of what was agreed upon?\n"
            + $"If YES -- you genuinely hold this same position -- respond with [CONFIRM] followed by a brief restatement in your own words.\n"
            + $"If NO -- this does NOT match your actual position -- respond with [REJECT] and clearly state what you ACTUALLY believe."
            + trumpReminder;
        var verifyA = await StreamResponse(chatA, verifyPromptA, nameA, ConsoleColor.Blue);

        // Ask B to verify A's stated consensus
        WriteRoundHeader($"VERIFICATION -- {nameB}");
        var verifyPromptB = $"CONSENSUS VERIFICATION: {nameA} stated the agreed consensus position as:\n\"\"\"\n{positionA}\n\"\"\"\n\n"
            + $"Does this accurately represent YOUR understanding of what was agreed upon?\n"
            + $"If YES -- you genuinely hold this same position -- respond with [CONFIRM] followed by a brief restatement in your own words.\n"
            + $"If NO -- this does NOT match your actual position -- respond with [REJECT] and clearly state what you ACTUALLY believe."
            + trumpReminder;
        var verifyB = await StreamResponse(chatB, verifyPromptB, nameB, ConsoleColor.Magenta);

        bool confirmA = verifyA.Contains("[CONFIRM]", StringComparison.OrdinalIgnoreCase);
        bool confirmB = verifyB.Contains("[CONFIRM]", StringComparison.OrdinalIgnoreCase);

        return (confirmA && confirmB, verifyA, verifyB);
    }

    // ════════════════════════════════════════════════════════════════════
    //  Impasse Reconciliation
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Forces one final attempt at finding common ground before accepting impasse.
    /// Both models must STILL include [IMPASSE] for the impasse to hold.
    /// </summary>
    static async Task<(bool StillImpasse, string ResponseA, string ResponseB)> AttemptReconciliationAsync(
        Chat chatA, string nameA,
        Chat chatB, string nameB,
        string topic,
        bool trumpMode = false)
    {
        PrintReconciliationAttempt();

        var trumpReminder = trumpMode
            ? "\n\nREMINDER: TRUMP MODE is still active. Stay in character -- attempt reconciliation but keep the trash talk and insults going strong. Even compromise should come with a side of savage."
            : "";

        var reconPrompt = $"Both debaters have declared an impasse on \"{topic}\". "
            + "Before this is accepted, you are REQUIRED to make one final, genuine attempt at finding common ground.\n\n"
            + "Focus specifically on:\n"
            + "1. What specific points DO you both agree on?\n"
            + "2. Is there a narrower or qualified version of the position you could both accept?\n"
            + "3. Can you propose a concrete compromise that addresses both sides' core concerns?\n\n"
            + $"If after this genuine attempt you STILL believe the disagreement is truly irreconcilable, "
            + $"include {ImpasseMarker} in your response and explain precisely why no compromise is possible.\n"
            + $"Otherwise, present your compromise proposal WITHOUT any {ImpasseMarker} or {ConsensusMarker} markers."
            + trumpReminder;

        WriteRoundHeader($"RECONCILIATION -- {nameA}");
        var reconA = await StreamResponse(chatA, reconPrompt, nameA, ConsoleColor.Blue);

        WriteRoundHeader($"RECONCILIATION -- {nameB}");
        var reconB = await StreamResponse(chatB, reconPrompt, nameB, ConsoleColor.Magenta);

        return (ContainsImpasse(reconA) && ContainsImpasse(reconB), reconA, reconB);
    }

    // ════════════════════════════════════════════════════════════════════
    //  Context Window Error Detection
    // ════════════════════════════════════════════════════════════════════

    static bool IsContextWindowError(Exception ex)
    {
        // Check the full exception chain for context-window related error messages
        // returned by Ollama when the context limit is hit.
        var current = ex;
        while (current != null)
        {
            var msg = current.Message;
            if (msg.Contains("context", StringComparison.OrdinalIgnoreCase)
                && (msg.Contains("exceed", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("too long", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("limit", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("overflow", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("window", StringComparison.OrdinalIgnoreCase)))
                return true;

            // Ollama may also return messages about input being too long or
            // running out of context / KV cache
            if (msg.Contains("out of context", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("kv cache", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("num_ctx", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("input is too long", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("prompt is too long", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("token limit", StringComparison.OrdinalIgnoreCase))
                return true;

            current = current.InnerException;
        }
        return false;
    }

    // ════════════════════════════════════════════════════════════════════
    //  UI Helpers
    // ════════════════════════════════════════════════════════════════════

    static void PrintHistoryDepth(Chat chatA, string modelA, Chat chatB, string modelB)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  [History] {modelA}: {chatA.Messages.Count} messages | {modelB}: {chatB.Messages.Count} messages");
        Console.ResetColor();
    }

    static string PromptModelSelection(IList<OllamaSharp.Models.Model> models, string label, string? defaultModel = null)
    {
        // Resolve default index (1-based) if the saved model still exists
        int? defaultIndex = null;
        if (!string.IsNullOrEmpty(defaultModel))
        {
            for (int i = 0; i < models.Count; i++)
            {
                if (string.Equals(models[i].Name, defaultModel, StringComparison.OrdinalIgnoreCase))
                {
                    defaultIndex = i + 1;
                    break;
                }
            }
        }

        while (true)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            if (defaultIndex.HasValue)
                Console.Write($"{label} [1-{models.Count}] (default: {defaultIndex} = {defaultModel}): ");
            else
                Console.Write($"{label} [1-{models.Count}]: ");
            Console.ResetColor();

            var input = Console.ReadLine()?.Trim();

            // Accept default on empty input
            if (string.IsNullOrEmpty(input) && defaultIndex.HasValue)
            {
                var selected = models[defaultIndex.Value - 1].Name;
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"  -> {selected}");
                Console.ResetColor();
                return selected;
            }

            if (int.TryParse(input, out int choice)
                && choice >= 1 && choice <= models.Count)
            {
                var selected = models[choice - 1].Name;
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"  -> {selected}");
                Console.ResetColor();
                return selected;
            }

            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("  Invalid selection. Please enter a number from the list.");
            Console.ResetColor();
        }
    }

    /// <summary>
    /// Derives a friendly debater name from an Ollama model string, preserving the tag.
    /// e.g. "llama3.2:8b-instruct-q4_K_M" -> "Llama3.2:8b-instruct-q4_K_M"
    ///      "deepseek-r1:14b" -> "Deepseek-R1:14b"
    ///      "qwen3:4b" -> "Qwen3:4b"
    /// </summary>
    static string GetDebaterName(string modelFullName)
    {
        string baseName, tag;
        if (modelFullName.Contains(':'))
        {
            var colonIdx = modelFullName.IndexOf(':');
            baseName = modelFullName[..colonIdx];
            tag = modelFullName[colonIdx..]; // includes the colon
        }
        else
        {
            baseName = modelFullName;
            tag = "";
        }

        // Capitalize first letter of each segment separated by - or _
        var parts = baseName.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length > 0)
                parts[i] = char.ToUpper(parts[i][0]) + parts[i][1..];
        }

        return string.Join("-", parts) + tag;
    }

    static void PrintBanner(bool trumpMode = false)
    {
        if (trumpMode)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine();
            Console.WriteLine("  +===================================================+");
            Console.WriteLine("  |            MASTER DEBATER                          |");
            Console.WriteLine("  |    \U0001f525 TRUMP MODE -- No Mercy, No Filter \U0001f525        |");
            Console.WriteLine("  +===================================================+");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine();
            Console.WriteLine("  +===================================================+");
            Console.WriteLine("  |            MASTER DEBATER                          |");
            Console.WriteLine("  |         AI vs AI -- Debate to Consensus            |");
            Console.WriteLine("  +===================================================+");
            Console.ResetColor();
        }
        Console.WriteLine();
    }

    static void PrintConsensusReached()
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("  +===================================================+");
        Console.WriteLine("  |        *** CONSENSUS REACHED ***                   |");
        Console.WriteLine("  |     Both debaters have agreed on a position!       |");
        Console.WriteLine("  +===================================================+");
        Console.ResetColor();
    }

    static void PrintImpasseReached()
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("  +===================================================+");
        Console.WriteLine("  |        *** IMPASSE DECLARED ***                    |");
        Console.WriteLine("  |   Both debaters agree they cannot find agreement.  |");
        Console.WriteLine("  +===================================================+");
        Console.ResetColor();
    }

    static void PrintVerifyingConsensus()
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  +===================================================+");
        Console.WriteLine("  |      VERIFYING CONSENSUS ...                       |");
        Console.WriteLine("  |   Cross-examining each debater's stated position   |");
        Console.WriteLine("  +===================================================+");
        Console.ResetColor();
    }

    static void PrintFalseConsensusDetected()
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("  +===================================================+");
        Console.WriteLine("  |      FALSE CONSENSUS DETECTED                      |");
        Console.WriteLine("  |   Debaters do not actually agree -- continuing     |");
        Console.WriteLine("  +===================================================+");
        Console.ResetColor();
    }

    static void PrintReconciliationAttempt()
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  +===================================================+");
        Console.WriteLine("  |      RECONCILIATION ATTEMPT                        |");
        Console.WriteLine("  |   Requiring one final attempt at common ground     |");
        Console.WriteLine("  +===================================================+");
        Console.ResetColor();
    }

    static void WriteDivider(char c)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  " + new string(c, 55));
        Console.ResetColor();
    }

    static void WriteRoundHeader(string text)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  -- {text} --");
        Console.ResetColor();
        Console.WriteLine();
    }

    static void WriteError(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  ERROR: {message}");
        Console.ResetColor();
    }
}
