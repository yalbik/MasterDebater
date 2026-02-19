using System.Text;
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
    private static readonly object ConsoleLock = new();

    static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        PrintBanner();

        // ── Connect to Ollama ──────────────────────────────────────────
        var ollamaUrl = args.Length > 0 ? args[0] : "http://localhost:11434";
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

        Console.WriteLine();
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
        var model1 = PromptModelSelection(models, "Select Debater 1");
        var model2 = PromptModelSelection(models, "Select Debater 2");

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
        var chatA = new Chat(clientA, BuildSystemPrompt(name1, model1, name2, model2))
        {
            Options = contextOptions
        };

        var clientB = new OllamaApiClient(uri);
        clientB.SelectedModel = model2;
        var chatB = new Chat(clientB, BuildSystemPrompt(name2, model2, name1, model1))
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
        bool consensusA = false;
        bool consensusB = false;
        bool reachedConsensus = false;

        try
        {

        // Opening argument
        WriteRoundHeader($"OPENING ARGUMENT -- {name1}");
        PrintHistoryDepth(chatA, name1, chatB, name2);
        lastResponseA = await StreamResponse(
            chatA,
            $"The debate topic is:\n\"{topic}\"\n\nPlease present your opening argument.",
            name1, ConsoleColor.Blue);

        consensusA = ContainsConsensus(lastResponseA);

        for (int round = 1; round <= MaxRounds; round++)
        {
            // ── Debater B responds ─────────────────────────────────────
            WriteRoundHeader($"ROUND {round} -- {name2} responds");

            string promptB = round == 1
                ? $"The debate topic is:\n\"{topic}\"\n\nYour opponent {name1} opened with:\n\n\"\"\"\n{lastResponseA}\n\"\"\"\n\nPresent your response and counter-arguments."
                : $"{name1} says:\n\n\"\"\"\n{lastResponseA}\n\"\"\"\n\nRespond to their arguments.";

            PrintHistoryDepth(chatA, name1, chatB, name2);
            lastResponseB = await StreamResponse(chatB, promptB, name2, ConsoleColor.Magenta);
            consensusB = ContainsConsensus(lastResponseB);

            if (consensusA && consensusB)
            {
                reachedConsensus = true;
                PrintConsensusReached();
                break;
            }

            // ── Debater A responds ─────────────────────────────────────
            WriteRoundHeader($"ROUND {round} -- {name1} responds");

            string promptA = $"{name2} says:\n\n\"\"\"\n{lastResponseB}\n\"\"\"\n\nRespond to their arguments.";

            PrintHistoryDepth(chatA, name1, chatB, name2);
            lastResponseA = await StreamResponse(chatA, promptA, name1, ConsoleColor.Blue);
            consensusA = ContainsConsensus(lastResponseA);

            if (consensusA && consensusB)
            {
                reachedConsensus = true;
                PrintConsensusReached();
                break;
            }

            if (round == MaxRounds)
            {
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  WARNING: Maximum rounds ({MaxRounds}) reached without full mutual consensus.");
                Console.ResetColor();
            }
        }

        // ── Generate summary ───────────────────────────────────────────
        Console.WriteLine();
        WriteDivider('=');
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  GENERATING DEBATE SUMMARY ...");
        Console.ResetColor();
        WriteDivider('-');
        Console.WriteLine();

        var summaryClient = new OllamaApiClient(uri);
        summaryClient.SelectedModel = model1;
        var summaryChat = new Chat(summaryClient, "You are an impartial debate analyst. Provide clear, concise summaries.")
        {
            Options = contextOptions
        };

        var consensusInstruction = reachedConsensus
            ? "Both debaters reached consensus. Please summarize the agreed-upon conclusion clearly and concisely. Highlight the key points both sides came to agree on."
            : "The debaters did not fully reach consensus within the allotted rounds. Please summarize the current state of the discussion: what they agree on, what they still disagree on, and what the most likely resolution would be.";

        var summaryPrompt = $"A debate was held on the following topic:\n\"{topic}\"\n\n"
            + $"The final position from {name1} ({model1}):\n\"\"\"\n{lastResponseA}\n\"\"\"\n\n"
            + $"The final position from {name2} ({model2}):\n\"\"\"\n{lastResponseB}\n\"\"\"\n\n"
            + consensusInstruction + "\n\n"
            + "Do NOT include any [CONSENSUS] tags in your summary.\nFormat your summary with clear sections.";

        Console.ForegroundColor = ConsoleColor.White;
        await foreach (var token in summaryChat.SendAsync(summaryPrompt))
        {
            Console.Write(token);
        }
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine();
        WriteDivider('=');

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(reachedConsensus
            ? "  Debate concluded with consensus."
            : "  Debate concluded (max rounds reached).");
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

    static string BuildSystemPrompt(string yourName, string yourModel, string opponentName, string opponentModel)
    {
        return $"""
            You are "{yourName}" in a structured AI debate.
            Your name is "{yourName}" and you are running model "{yourModel}".
            Your opponent's name is "{opponentName}" and they are running model "{opponentModel}".
            Always refer to yourself as "{yourName}" and your opponent as "{opponentName}".

            === YOUR OBJECTIVE ===
            - Present clear, well-reasoned arguments on the given debate topic.
            - Carefully consider {opponentName}'s points and respond directly to them.
            - Work toward finding COMMON GROUND and reaching genuine consensus with {opponentName}.
            - Be willing to update or refine your position when presented with compelling arguments.
            - You are trying to reach agreement, not "win" -- the goal is the best answer collectively.

            === RESPONSE FORMAT ===
            1. Keep responses focused and concise (2-4 paragraphs).
            2. Start by directly addressing {opponentName}'s latest arguments before introducing new points.
            3. Clearly state where you agree and where you still disagree with {opponentName}.
            4. Address {opponentName} by name when referring to their arguments.

            === CONSENSUS PROTOCOL ===
            When -- and ONLY when -- you genuinely believe you and {opponentName} have reached a
            shared conclusion on the core question, end your response with the exact marker:

                {ConsensusMarker}

            After the marker, write a one-paragraph summary of the agreed-upon position.
            Do NOT use {ConsensusMarker} prematurely or just to end the debate early.
            Both debaters must independently include {ConsensusMarker} for the debate to conclude.

            === IMPORTANT ===
            - Be respectful but intellectually rigorous.
            - Avoid repeating points already conceded.
            - Focus on substance, not rhetoric.
            - The debate ends only when BOTH you and {opponentName} include {ConsensusMarker} in your latest responses.
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

        try
        {
            await foreach (var token in chat.SendAsync(message))
            {
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

        lock (ConsoleLock)
        {
            Console.WriteLine();
            Console.ForegroundColor = color;
            Console.WriteLine("  +-------------------------------------------------------");
            Console.ResetColor();
        }

        return sb.ToString();
    }

    // ════════════════════════════════════════════════════════════════════
    //  Consensus Detection
    // ════════════════════════════════════════════════════════════════════

    static bool ContainsConsensus(string response)
    {
        return response.Contains(ConsensusMarker, StringComparison.OrdinalIgnoreCase);
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

    static string PromptModelSelection(IList<OllamaSharp.Models.Model> models, string label)
    {
        while (true)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write($"{label} [1-{models.Count}]: ");
            Console.ResetColor();

            if (int.TryParse(Console.ReadLine()?.Trim(), out int choice)
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
    /// Derives a friendly debater name from an Ollama model string.
    /// e.g. "llama3.2:8b-instruct-q4_K_M" -> "Llama3.2"
    ///      "deepseek-r1:14b" -> "Deepseek-R1"
    ///      "qwen3:4b" -> "Qwen3"
    /// </summary>
    static string GetDebaterName(string modelFullName)
    {
        // Strip everything after the colon (tag/quantization info)
        var baseName = modelFullName.Contains(':')
            ? modelFullName[..modelFullName.IndexOf(':')]
            : modelFullName;

        // Capitalize first letter of each segment separated by - or _
        var parts = baseName.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length > 0)
                parts[i] = char.ToUpper(parts[i][0]) + parts[i][1..];
        }

        return string.Join("-", parts);
    }

    static void PrintBanner()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine();
        Console.WriteLine("  +===================================================+");
        Console.WriteLine("  |            MASTER DEBATER                          |");
        Console.WriteLine("  |         AI vs AI -- Debate to Consensus            |");
        Console.WriteLine("  +===================================================+");
        Console.ResetColor();
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
