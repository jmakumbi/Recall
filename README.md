# recall

A self-contained Windows CLI that lets you chat with your personal corpus of files using natural language. All data stays on your machine — no cloud calls during operation.

```
╔══════════════════════════════════════════════╗
║  recall — your personal memory               ║
║  KB: 42 files · 1 204 chunks                 ║
║  Type /help for commands                     ║
╚══════════════════════════════════════════════╝
```

---

## Prerequisites

| Requirement | Minimum |
|---|---|
| Windows | 10 x64 or later |
| [Ollama](https://ollama.com) | Latest stable |
| [Everything](https://www.voidtools.com/) | 1.4+ (optional — improves search speed) |

> `.NET 8` is bundled — a separate runtime install is not required.

---

## Supported File Types

recall uses two extraction methods, applied in order. Only plain text is extracted — no images or embedded media are processed.

### Tier 1 — Open XML (built-in, always works)

These formats are ZIP-based archives and are extracted using .NET's built-in compression library. No extra software required.

| Extension | Format |
|---|---|
| `.docx` | Microsoft Word (2007 and later) |
| `.xlsx` | Microsoft Excel (2007 and later) |
| `.pptx` | Microsoft PowerPoint (2007 and later) |
| `.txt` `.md` `.csv` `.log` | Plain text variants |

### Tier 2 — Windows IFilter (requires registered COM handler)

IFilter is a Windows COM interface that extracts text from arbitrary file types. It works when the corresponding IFilter handler is installed and registered on your machine. The handler is usually installed alongside the application that owns the format.

| Extension | Format | Typical handler |
|---|---|---|
| `.pdf` | Adobe PDF | Adobe Acrobat / Windows PDF viewer |
| `.msg` | Outlook email | Microsoft Office |
| `.doc` `.xls` `.ppt` | Legacy Office (pre-2007 binary) | Microsoft Office |
| `.eml` | Email (MIME) | Windows Search built-in |
| `.html` `.htm` | HTML | Windows Search built-in |
| `.rtf` | Rich Text Format | Windows Search built-in |
| Any other extension | — | Whatever IFilter is registered for that type |

> **OneDrive users:** IFilter opens the file at the OS level, which can trigger a network sync for cloud-only files. recall handles this gracefully — if IFilter doesn't respond within 15 seconds the file is skipped with a log message. Ensure files are locally available for best results.

> **Click-to-Run Office:** The Office IFilter shipped with Click-to-Run installations may not respond to out-of-process COM calls. In that case recall automatically falls back to the Open XML extractor for `.docx`, `.xlsx`, and `.pptx` files.

### Not supported

- `.doc`, `.xls`, `.ppt` (legacy binary Office) when no IFilter is installed
- `.pdf` when no PDF IFilter is installed (e.g. no Acrobat or Windows PDF handler)
- Binary files: images, audio, video, executables, archives
- Password-protected documents

---

## Native Libraries (required)

Two DLLs must be placed in the `libs\` folder alongside `recall.exe` before running.

### `vec0.dll`

Used for vector similarity search via sqlite-vec.

1. Go to <https://github.com/asg017/sqlite-vec/releases>
2. Download the latest Windows x64 loadable release (e.g. `sqlite-vec-*-loadable-windows-x86_64.tar.gz`)
3. Extract and copy `vec0.dll` into `libs\`

### `Everything64.dll` (optional)

Used for fast file discovery via the Everything search engine.

1. Go to <https://www.voidtools.com/downloads/>
2. Under **"Download Everything SDK"**, download the SDK zip
3. Extract and copy `Everything64.dll` into `libs\`
4. Install and run Everything so the IPC server is available

> Without `Everything64.dll`, recall falls back to Windows Desktop Search (WDS), which is always available on Windows 10/11 but may not index every folder.

---

## Installation

```powershell
# Run once from the recall directory
.\install.ps1
```

The installer will:

1. Check for Ollama and download it if missing
2. Pull the embedding model (`nomic-embed-text`)
3. Pull the default chat model (see `appsettings.json → Ollama.ChatModel`)
4. Verify `libs\vec0.dll` is present
5. Copy files to `%APPDATA%\recall\` and add it to your PATH

---

## First Run

```powershell
recall
```

On first launch recall will:

- Create `%APPDATA%\recall\recall.db` automatically
- Check that Ollama is running and both models are available
- Show the startup banner with an empty KB (0 files, 0 chunks)
- Prompt you to run `/setup` if no search paths are configured

Configure which folders to search:

```
recall> /setup
```

Then ingest your first files:

```
recall> /ingest quarterly report
```

recall will search for matching files, show you the candidates, and ask whether to ingest them.

---

## Command Reference

| Command | Description |
|---|---|
| `/setup` | Configure which folders recall searches |
| `/help` | Show command list |
| `/search <query>` | Search for files without ingesting |
| `/ingest <query>` | Find files and add new/stale ones to the KB |
| `/kb` | Show KB stats (files, chunks, last updated) |
| `/clear` | Clear the current conversation context |
| `/forget <path>` | Remove a file and its chunks from the KB |
| `/exit` or `/quit` | Exit recall |
| _anything else_ | Chat query against your knowledge base |

### Colour coding in `/search` and `/ingest`

| Colour | Meaning |
|---|---|
| Green | File is already in the knowledge base and up to date |
| Yellow | File is in the KB but has been modified — will be re-ingested |
| Dim | File has not been ingested yet |

---

## Privacy

- All embeddings and chat inference run locally via Ollama
- No data is sent to any external service during normal operation
- The only network calls are Ollama model downloads (`ollama pull …`) during setup
- `recall.db` lives in `%APPDATA%\recall\` and is never transmitted anywhere

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

## Models

| Role | Default | Notes |
|---|---|---|
| Embeddings | `nomic-embed-text` (768-dim) | Required. Do not change — chunk vectors in the DB are tied to this model's dimensions. |
| Chat | `qwen3:14b` | Change freely. Any Ollama model works. |

To use a different chat model, edit `appsettings.json` → `Ollama.ChatModel` and restart.

```powershell
# Example: switch to a smaller model
ollama pull qwen3:8b
# then set "ChatModel": "qwen3:8b" in appsettings.json
```

> If you change the embedding model, delete `recall.db` and re-ingest all files — embeddings from different models are not compatible.

---

## Configuration (`appsettings.json`)

```json
{
  "Recall": {
    "DbPath": "%APPDATA%\\recall\\recall.db",
    "Vec0DllPath": "libs\\vec0.dll",
    "EverythingDllPath": "libs\\Everything64.dll",
    "SearchPaths": []
  },
  "Ollama": {
    "BaseUrl": "http://localhost:11434",
    "EmbeddingModel": "nomic-embed-text",
    "ChatModel": "qwen3:14b",
    "ChatContextWindow": 8192
  },
  "Ingestion": {
    "ChunkSize": 512,
    "ChunkOverlap": 100,
    "MaxExtractedCharsPerFile": 500000
  },
  "Retrieval": {
    "TopK": 5,
    "MaxDistance": 1.0,
    "MaxContextChars": 6000
  }
}
```

`SearchPaths` is populated by `/setup`. `%APPDATA%` and `%USERPROFILE%` are expanded at runtime everywhere.

---

## Author

James S. K. Makumbi
