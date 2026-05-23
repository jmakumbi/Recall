# recall

A self-contained Windows CLI that lets you chat with your personal corpus of files using natural language. All data stays on your machine — no cloud calls during operation.

```
╔══════════════════════════════════╗
║  recall — your personal memory  ║
║  KB: 42 files · 1 204 chunks    ║
║  Type /help for commands         ║
╚══════════════════════════════════╝
```

---

## Prerequisites

| Requirement | Minimum |
|---|---|
| Windows | 10 x64 or later |
| [Ollama](https://ollama.com) | Latest stable |
| [Everything](https://www.voidtools.com/) | 1.4+ (optional but recommended) |

> `.NET 8` is bundled — a separate runtime install is not required.

---

## Native Libraries (required)

Two DLLs must be placed in the `libs\` folder alongside `recall.exe` before running.

### `Everything64.dll`

Used for fast file discovery via the Everything search engine.

1. Go to <https://www.voidtools.com/downloads/>
2. Under **"Download Everything SDK"**, download the SDK zip
3. Extract and copy `Everything64.dll` into `libs\`

> Everything does not need to be running as a service. recall will use it in standalone mode and warn you the first time.

### `vec0.dll`

Used for vector similarity search via sqlite-vec.

1. Go to <https://github.com/asg017/sqlite-vec/releases>
2. Download the latest Windows x64 release zip
3. Extract and copy `vec0.dll` into `libs\`

---

## Installation

```powershell
# Run once from the recall directory
.\install.ps1
```

The installer will:

1. Check for Ollama and download it if missing
2. Pull the embedding model (`nomic-embed-text`)
3. Pull the chat model (`qwen3:8b`, ~5.2 GB)
4. Verify `libs\Everything64.dll` and `libs\vec0.dll` are present
5. Create `%APPDATA%\Recall\` for the database

---

## First Run

```powershell
.\recall.exe
```

On first launch recall will:

- Create `%APPDATA%\Recall\recall.db` automatically
- Check that Ollama is running and both models are available
- Show the startup banner with an empty KB (0 files, 0 chunks)

To ingest your first files:

```
> /ingest quarterly report
```

recall will search for matching files, show you the candidates, and ask which ones to add to the knowledge base.

---

## Command Reference

| Command | Description |
|---|---|
| `/help` | Show command list |
| `/search <query>` | Search for files without ingesting |
| `/ingest <query>` | Find files and add selected ones to the KB |
| `/kb` | Show KB stats (files, chunks, last updated) |
| `/clear` | Clear the current conversation context |
| `/forget <path>` | Remove a file and its chunks from the KB |
| `/exit` or `/quit` | Exit recall |
| _anything else_ | Chat query against your knowledge base |

### Candidate Selection

After `/ingest` you will see a numbered list:

```
Found 4 candidates:
  [1]  Proposal_v2.docx       C:\Clients\Vaza\    48 KB    2 days ago   ✓ in KB
  [2]  Meeting_Notes.docx     C:\Clients\Vaza\    12 KB    5 days ago   ✗ not in KB
  [3]  Email_Thread.msg       C:\Mail\Archive\    31 KB    1 week ago   ✗ not in KB
  [4]  Dev_Notes.txt          C:\Dev\             6 KB     3 days ago   ✗ not in KB

Ingest which? (1,3 / all / skip):
```

- **Green** — already in KB
- **Yellow** — in KB but file has changed (will be re-ingested)
- **White** — not yet ingested

---

## Privacy

- All embeddings and chat inference run locally via Ollama
- No data is sent to any external service during normal operation
- The only network calls are Ollama model downloads (`ollama pull …`) during setup
- `recall.db` lives in `%APPDATA%\Recall\` and is never transmitted anywhere

---

## Build from Source

Requires .NET 8 SDK.

```powershell
dotnet publish Recall.Cli/Recall.Cli.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=false `
  -o ./publish
```

The post-publish step copies `libs\` and `appsettings.json` into `./publish` automatically.

---

## Models Used

| Role | Model | Provider |
|---|---|---|
| Embeddings | `nomic-embed-text` (768-dim) | Ollama (local) |
| Chat | `qwen3:8b` | Ollama (local) |

To change the chat model, edit `appsettings.json` → `Ollama.ChatModel` and restart.

---

## Configuration (`appsettings.json`)

```json
{
  "Ollama": {
    "BaseUrl": "http://localhost:11434",
    "EmbeddingModel": "nomic-embed-text",
    "ChatModel": "qwen3:8b",
    "ChatContextWindow": 8192
  },
  "Storage": {
    "DbPath": "%APPDATA%\\Recall\\recall.db"
  },
  "Ingestion": {
    "ChunkSize": 512,
    "ChunkOverlap": 100,
    "MaxExtractedCharsPerFile": 500000
  },
  "Discovery": {
    "EverythingDllPath": "libs\\Everything64.dll",
    "DefaultSearchScope": "%USERPROFILE%"
  },
  "Retrieval": {
    "TopK": 10,
    "MinSimilarityScore": 0.3
  }
}
```

`%APPDATA%` and `%USERPROFILE%` are expanded at runtime.

---

## Author

James S. K. Makumbi
