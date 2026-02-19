# MasterDebater

AI vs AI debate engine powered by [Ollama](https://ollama.com). Pick two local LLMs, give them a topic, and watch them argue it out until they reach consensus -- or declare an impasse.

## Features

- **Structured debate** between any two Ollama models running locally
- **Consensus protocol** -- models work toward genuine agreement with cross-verification to prevent false consensus
- **Impasse protocol** -- when agreement is impossible, models can declare an impasse (with a mandatory reconciliation attempt first)
- **Trump mode** -- enables insults, trash talk, and no-holds-barred rhetorical combat between models
- **Streaming output** -- responses are streamed token-by-token with colored, formatted output
- **Thinking support** -- models that expose chain-of-thought (e.g. DeepSeek-R1) display their reasoning
- **Context window handling** -- graceful detection and reporting when a model exceeds its context limit
- **Automatic debate summaries** -- both models summarize their final positions after the debate concludes

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (or later)
- [Ollama](https://ollama.com) running locally (or on an accessible host)
- At least 2 models pulled in Ollama (e.g. `ollama pull llama3.2`, `ollama pull qwen3`)

## Build

```
git clone https://github.com/YOUR_USERNAME/MasterDebater-Claude.git
cd MasterDebater-Claude
dotnet build
```

## Usage

```
dotnet run --project MasterDebater [ollama-url] [--trump]
```

### Arguments

| Argument | Description | Default |
|---|---|---|
| `ollama-url` | URL of the Ollama API | `http://localhost:11434` |
| `--trump` | Enable Trump mode (insults and trash talk between models) | off |

### Examples

Run with default settings (Ollama on localhost):

```
dotnet run --project MasterDebater
```

Connect to a remote Ollama instance:

```
dotnet run --project MasterDebater http://192.168.1.50:11434
```

Enable Trump mode:

```
dotnet run --project MasterDebater --trump
```

Both options together:

```
dotnet run --project MasterDebater http://192.168.1.50:11434 --trump
```

### Interactive prompts

After launch, you will be prompted to:

1. Enter a debate topic
2. Select Debater 1 from your installed models
3. Select Debater 2 from your installed models

The debate then runs automatically.

## How it works

Each debater gets a system prompt establishing the debate rules, their identity, and their opponent's identity. The models take turns responding, with full conversation history sent on each request (so models can unload/reload between turns without losing context).

**Consensus:** When both models include a `[CONSENSUS]` marker, a verification round cross-examines each debater against the other's stated position. If both confirm, the debate ends. If not, the false consensus is flagged and debate continues.

**Impasse:** When both models include an `[IMPASSE]` marker (after a minimum of 3 rounds), a reconciliation round forces one final attempt at compromise. If both still declare impasse, it is accepted. Otherwise, debate continues.

**Trump mode:** Models are instructed to insult each other -- attacking arguments, questioning intelligence, mocking reasoning, and optionally roasting each other's technical details (architecture, parameter count, training data, benchmarks, corporate lineage, etc.). Substantive arguments are still required underneath the trash talk.

## Configuration

Key constants in `Program.cs`:

| Constant | Value | Description |
|---|---|---|
| `MaxRounds` | 20 | Maximum debate rounds before forced termination |
| `DefaultContextWindow` | 32768 | Token context window passed to each model |
| `MinRoundsForImpasse` | 3 | Minimum rounds before impasse can be declared |

## License

This project is licensed under the [GNU General Public License v3.0](https://www.gnu.org/licenses/gpl-3.0.html).
