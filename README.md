# MasterDebater

AI vs AI debate engine. Pick two LLMs, give them a topic, and watch them argue it out until they reach consensus -- or declare an impasse. Supports [Ollama](https://ollama.com) as well as any OpenAI-compatible server (LM Studio, vLLM, llama.cpp, OpenAI, etc.).

## Features

- **Structured debate** between any two models -- via Ollama or any OpenAI-compatible server
- **Consensus protocol** -- models work toward genuine agreement with cross-verification to prevent false consensus
- **Impasse protocol** -- when agreement is impossible, models can declare an impasse (with a mandatory reconciliation attempt first)
- **Trump mode** -- enables insults, trash talk, and no-holds-barred rhetorical combat between models
- **Streaming output** -- responses are streamed token-by-token with colored, formatted output
- **Thinking support** -- models that expose chain-of-thought (e.g. DeepSeek-R1) display their reasoning
- **Context window handling** -- graceful detection and reporting when a model exceeds its context limit
- **Automatic debate summaries** -- both models summarize their final positions after the debate concludes

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (or later)
- **Ollama mode:** [Ollama](https://ollama.com) running locally or on an accessible host, with at least 2 models pulled (e.g. `ollama pull llama3.2`, `ollama pull qwen3`)
- **OpenAI mode:** any OpenAI-compatible server (LM Studio, vLLM, llama.cpp, OpenAI API, etc.) or a real OpenAI API key

## Build

```
git clone https://github.com/YOUR_USERNAME/MasterDebater-Claude.git
cd MasterDebater-Claude
dotnet build
```

## Usage

```
dotnet run --project MasterDebater [url] [--openai] [--api-key <key>] [--trump]
```

### Arguments

| Argument | Description | Default |
|---|---|---|
| `url` | Backend URL | `http://localhost:11434` |
| `--openai` | Use OpenAI-compatible API instead of Ollama | off |
| `--api-key <key>` | API key for OpenAI-compatible servers that require one | `ollama` |
| `--trump` | Enable Trump mode (insults and trash talk between models) | off |

### Ollama mode

Ollama mode is the default. No extra flags needed.

```
# Local Ollama (default)
dotnet run --project MasterDebater

# Remote Ollama
dotnet run --project MasterDebater http://192.168.1.50:11434

# Local Ollama + Trump mode
dotnet run --project MasterDebater --trump
```

### OpenAI-compatible mode

Pass `--openai` to switch to the OpenAI-compatible API. The URL should point to the base of the API (including `/v1` if required by the server).

```
# LM Studio (default port)
dotnet run --project MasterDebater http://localhost:1234/v1 --openai

# vLLM or llama.cpp server
dotnet run --project MasterDebater http://localhost:8000/v1 --openai

# Real OpenAI API
dotnet run --project MasterDebater https://api.openai.com --openai --api-key sk-...

# OpenAI-compatible server + Trump mode
dotnet run --project MasterDebater http://localhost:1234/v1 --openai --trump
```

> **API key:** local servers (LM Studio, vLLM, llama.cpp) don't require a real key -- any non-empty string works and `ollama` is used by default. Only pass `--api-key` when connecting to a service that requires authentication.

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
| `DefaultContextWindow` | 32768 | Token context window passed to each model (Ollama mode only) |
| `MinRoundsForImpasse` | 3 | Minimum rounds before impasse can be declared |

## License

This project is licensed under the [GNU General Public License v3.0](https://www.gnu.org/licenses/gpl-3.0.html).
