using Recall.Discovery;
using Recall.Ingestion;
using Recall.Ollama;
using Recall.Retrieval;
using Recall.Storage;
using Spectre.Console;

namespace Recall.Cli;

/// <summary>
/// Main interactive REPL. Manages the conversation loop, dispatches slash commands,
/// and orchestrates discovery → ingestion → retrieval → chat flows.
/// </summary>
public sealed class Repl
{
    private readonly TrackerDb         _db;
    private readonly VectorStore       _vectors;
    private readonly OllamaClient      _ollama;
    private readonly DiscoveryService  _discovery;
    private readonly IngestionPipeline _pipeline;
    private readonly Retriever         _retriever;
    private readonly IntentClassifier  _classifier;
    private readonly AppConfig         _appConfig;

    // Conversation history for multi-turn chat
    private readonly List<ChatMessage> _history = [];

    public Repl(
        TrackerDb         db,
        VectorStore       vectors,
        OllamaClient      ollama,
        DiscoveryService  discovery,
        IngestionPipeline pipeline,
        Retriever         retriever,
        IntentClassifier  classifier,
        AppConfig         appConfig)
    {
        _db         = db;
        _vectors    = vectors;
        _ollama     = ollama;
        _discovery  = discovery;
        _pipeline   = pipeline;
        _retriever  = retriever;
        _classifier = classifier;
        _appConfig  = appConfig;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        PrintBanner();

        if (_appConfig.SearchPaths.Length == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No search paths configured — run [bold]/setup[/] to get started.[/]");
            AnsiConsole.WriteLine();
        }

        while (!ct.IsCancellationRequested)
        {
            string? input;
            try
            {
                input = AnsiConsole.Prompt(
                    new TextPrompt<string>("[bold cyan]recall>[/]")
                        .AllowEmpty());
            }
            catch (Exception)
            {
                break; // EOF / Ctrl+C
            }

            if (string.IsNullOrWhiteSpace(input)) continue;

            try
            {
                var (intent, commandName, args) = _classifier.Classify(input);

                switch (intent)
                {
                    case Intent.Command:
                        await HandleCommandAsync(commandName!, args, ct);
                        break;

                    case Intent.Discovery:
                        await HandleDiscoveryAsync(args, ct);
                        break;

                    case Intent.Ambiguous:
                        var choice = AnsiConsole.Prompt(
                            new SelectionPrompt<string>()
                                .Title("Search for [green]files[/] or query your [blue]knowledge base[/]?")
                                .AddChoices("Search files (discovery)", "Query knowledge base (chat)"));
                        if (choice.StartsWith("Search"))
                            await HandleDiscoveryAsync(args, ct);
                        else
                            await HandleChatAsync(args, ct);
                        break;

                    case Intent.Chat:
                    default:
                        await HandleChatAsync(args, ct);
                        break;
                }
            }
            catch (OllamaUnavailableException ex)
            {
                AnsiConsole.MarkupLine($"[red]Ollama is unavailable:[/] {ex.Message}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            }

            AnsiConsole.WriteLine();
        }

        AnsiConsole.MarkupLine("[dim]Goodbye.[/]");
    }

    // ── Slash commands ────────────────────────────────────────────────────────

    private async Task HandleCommandAsync(string name, string args, CancellationToken ct)
    {
        switch (name)
        {
            case "help":
                PrintHelp();
                break;

            case "setup":
                await RunSetupWizardAsync(ct);
                break;

            case "search":
                await HandleDiscoveryAsync(args, ct);
                break;

            case "ingest":
                await HandleIngestCommandAsync(args, ct);
                break;

            case "kb":
                PrintKbStats();
                break;

            case "clear":
                _history.Clear();
                AnsiConsole.MarkupLine("[dim]Conversation context cleared.[/]");
                break;

            case "forget":
                HandleForget(args);
                break;

            case "exit":
            case "quit":
                Environment.Exit(0);
                break;

            default:
                AnsiConsole.MarkupLine($"[yellow]Unknown command:[/] /{name}  (try [bold]/help[/])");
                break;
        }
    }

    // ── Discovery ─────────────────────────────────────────────────────────────

    private async Task HandleDiscoveryAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            AnsiConsole.MarkupLine("[yellow]Please provide a search query.[/]");
            return;
        }

        if (_appConfig.SearchPaths.Length == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No search paths configured — run [bold]/setup[/] first.[/]");
            return;
        }

        List<DiscoveryResult> results = [];
        await AnsiConsole.Status()
            .StartAsync("Searching…", async _ =>
            {
                results = (await Task.Run(() => _discovery.Search(query), ct)).ToList();
            });

        if (results.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No files found.[/]");
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("#")
            .AddColumn("File")
            .AddColumn("Status")
            .AddColumn("Path");

        for (int i = 0; i < results.Count; i++)
        {
            var r      = results[i];
            var status = r.AlreadyIngested
                ? (r.IsStale ? "[yellow]stale[/]" : "[green]in KB[/]")
                : "[dim]not ingested[/]";
            table.AddRow(
                (i + 1).ToString(),
                Markup.Escape(r.FileName),
                status,
                Markup.Escape(ShortenPath(r.FullPath)));
        }
        AnsiConsole.Write(table);

        // Offer to ingest un-ingested / stale files
        var toIngest = results
            .Where(r => !r.AlreadyIngested || r.IsStale)
            .ToList();

        if (toIngest.Count > 0)
        {
            bool doIngest = AnsiConsole.Confirm($"Ingest {toIngest.Count} new/stale file(s)?", defaultValue: false);
            if (doIngest)
                await IngestFilesAsync(toIngest.Select(r => r.FullPath).ToList(), ct);
        }
    }

    // ── /ingest command ───────────────────────────────────────────────────────

    private async Task HandleIngestCommandAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            AnsiConsole.MarkupLine("[yellow]Usage: /ingest <search query>[/]");
            return;
        }

        await HandleDiscoveryAsync(query, ct);
    }

    // ── Chat ──────────────────────────────────────────────────────────────────

    private async Task HandleChatAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return;

        // Retrieve relevant context
        List<ChunkResult> chunks = [];
        await AnsiConsole.Status()
            .StartAsync("Retrieving context…", async _ =>
            {
                chunks = await _retriever.QueryAsync(query, ct);
            });

        var context = _retriever.AssembleContext(chunks);

        // Build messages
        var messages = new List<ChatMessage>
        {
            ChatMessage.System(OllamaClient.SystemPrompt +
                (context.Length > 0
                    ? $"\n\nContext from user's files:\n{context}"
                    : "\n\nNo relevant documents found in the knowledge base."))
        };
        messages.AddRange(_history);
        messages.Add(ChatMessage.User(query));

        // Stream the response
        AnsiConsole.Markup("[bold blue]▸ recall:[/] ");
        var responseTokens = new System.Text.StringBuilder();

        await foreach (var token in _ollama.ChatAsync(messages, ct))
        {
            AnsiConsole.Write(Markup.Escape(token));
            responseTokens.Append(token);
        }
        AnsiConsole.WriteLine();

        // Show sources
        if (chunks.Count > 0)
        {
            var sources = chunks
                .Select(c => c.FileName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            AnsiConsole.MarkupLine($"[dim][Sources: {string.Join(", ", sources.Select(Markup.Escape))}][/]");
        }

        // Persist to conversation history
        _history.Add(ChatMessage.User(query));
        _history.Add(ChatMessage.Assistant(responseTokens.ToString()));

        // Cap history to last 10 exchanges (20 messages)
        while (_history.Count > 20)
            _history.RemoveAt(0);
    }

    // ── Ingest helper ─────────────────────────────────────────────────────────

    private async Task IngestFilesAsync(List<string> paths, CancellationToken ct)
    {
        int lastChunks = 0;
        var progress = new Progress<IngestionProgress>(p =>
        {
            if (p.Phase == "done" || p.ChunksEmitted != lastChunks)
            {
                lastChunks = p.ChunksEmitted;
                AnsiConsole.MarkupLine(
                    $"  [[{p.FileIndex}/{p.TotalFiles}]] [dim]{p.Phase,-12}[/] " +
                    $"[green]{Markup.Escape(p.FileName)}[/]  chunks={p.ChunksEmitted}");
            }
        });

        await AnsiConsole.Status()
            .StartAsync("Ingesting files…", async ctx =>
            {
                await _pipeline.IngestAsync(paths, progress, ct);
                ctx.Status = "Done";
            });

        var stats = _db.GetKbStats();
        AnsiConsole.MarkupLine(
            $"[green]KB updated:[/] {stats.TotalFiles} file(s), {stats.TotalChunks} chunk(s)");
    }

    // ── /kb stats ─────────────────────────────────────────────────────────────

    private void PrintKbStats()
    {
        var s = _db.GetKbStats();
        var panel = new Panel(
            $"[bold]Files:[/] {s.TotalFiles}\n" +
            $"[bold]Chunks:[/] {s.TotalChunks}\n" +
            $"[bold]Last updated:[/] {(s.LastUpdated.HasValue ? s.LastUpdated.Value.ToString("yyyy-MM-dd HH:mm") : "never")}")
            .Header("[bold]Knowledge Base[/]")
            .Border(BoxBorder.Rounded);
        AnsiConsole.Write(panel);
    }

    // ── /forget ───────────────────────────────────────────────────────────────

    private void HandleForget(string pathArg)
    {
        if (string.IsNullOrWhiteSpace(pathArg))
        {
            AnsiConsole.MarkupLine("[yellow]Usage: /forget <file path>[/]");
            return;
        }
        var path = Path.GetFullPath(pathArg.Trim('"'));
        var tracked = _db.GetTrackedFile(path);
        if (tracked is null)
        {
            AnsiConsole.MarkupLine($"[yellow]File not in KB:[/] {Markup.Escape(path)}");
            return;
        }
        _vectors.DeleteChunks(tracked.VecRowIds);
        _db.DeleteFile(path);
        AnsiConsole.MarkupLine($"[green]Removed from KB:[/] {Markup.Escape(Path.GetFileName(path))}");
    }

    // ── /setup wizard ─────────────────────────────────────────────────────────

    private async Task RunSetupWizardAsync(CancellationToken ct)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold]Search Path Setup[/]").LeftJustified());
        AnsiConsole.WriteLine();

        var paths = _appConfig.SearchPaths.ToList();

        if (paths.Count > 0)
        {
            AnsiConsole.MarkupLine("[bold]Current search paths:[/]");
            for (int i = 0; i < paths.Count; i++)
                AnsiConsole.MarkupLine($"  {i + 1}. {Markup.Escape(paths[i])}");
            AnsiConsole.WriteLine();
        }
        else
        {
            AnsiConsole.MarkupLine("[dim]No search paths configured yet.[/]");
            AnsiConsole.WriteLine();
        }

        bool done = false;
        while (!done && !ct.IsCancellationRequested)
        {
            var choices = new List<string> { "Add folder", "Done" };
            if (paths.Count > 0)
                choices.Insert(1, "Remove folder");

            var action = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("What would you like to do?")
                    .AddChoices(choices));

            switch (action)
            {
                case "Add folder":
                {
                    var raw = AnsiConsole.Ask<string>("Enter folder path (supports %APPDATA%, %USERPROFILE%, etc.):");
                    var expanded = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
                    if (!Directory.Exists(expanded))
                    {
                        AnsiConsole.MarkupLine($"[red]Directory not found:[/] {Markup.Escape(expanded)}");
                    }
                    else if (paths.Any(p => p.Equals(expanded, StringComparison.OrdinalIgnoreCase)))
                    {
                        AnsiConsole.MarkupLine("[yellow]Path already in the list.[/]");
                    }
                    else
                    {
                        paths.Add(expanded);
                        AnsiConsole.MarkupLine($"[green]Added:[/] {Markup.Escape(expanded)}");
                    }
                    break;
                }

                case "Remove folder":
                {
                    if (paths.Count == 0) break;
                    var toRemove = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("Select path to remove:")
                            .AddChoices(paths.Select(Markup.Escape)));
                    // match back to original
                    var match = paths.FirstOrDefault(p => Markup.Escape(p) == toRemove) ?? toRemove;
                    paths.Remove(match);
                    AnsiConsole.MarkupLine($"[green]Removed:[/] {Markup.Escape(match)}");
                    break;
                }

                case "Done":
                    done = true;
                    break;
            }
        }

        _appConfig.SearchPaths = [.. paths];
        await _appConfig.SaveAsync();

        AnsiConsole.MarkupLine($"[green]Saved {paths.Count} search path(s).[/]");
        AnsiConsole.WriteLine();

        // Reload DiscoveryService with updated paths
        _discovery.UpdateSearchPaths([.. paths]);
    }

    // ── Banner & Help ─────────────────────────────────────────────────────────

    private void PrintBanner()
    {
        AnsiConsole.Write(new FigletText("recall").Color(Color.Cyan1));
        AnsiConsole.MarkupLine("[dim]Your personal knowledge base — powered by local Ollama LLMs[/]");
        AnsiConsole.WriteLine();

        PrintKbStats();
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("Type a question or use [bold]/help[/] to see available commands.");
        AnsiConsole.WriteLine();
    }

    private static void PrintHelp()
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Command[/]")
            .AddColumn("[bold]Description[/]");

        table.AddRow("/setup",           "Configure search paths interactively");
        table.AddRow("/search <query>",  "Discover files matching query");
        table.AddRow("/ingest <query>",  "Discover files and ingest new/stale ones");
        table.AddRow("/kb",              "Show knowledge base statistics");
        table.AddRow("/clear",           "Clear conversation context");
        table.AddRow("/forget <path>",   "Remove a file from the knowledge base");
        table.AddRow("/help",            "Show this help");
        table.AddRow("/exit",            "Exit recall");
        table.AddRow("[dim]<question>[/]", "[dim]Query the knowledge base (default)[/]");

        AnsiConsole.Write(table);
    }

    // ── Utilities ─────────────────────────────────────────────────────────────

    private static string ShortenPath(string path, int maxLen = 60)
    {
        if (path.Length <= maxLen) return path;
        var parts = path.Split(Path.DirectorySeparatorChar);
        if (parts.Length <= 3) return path[..maxLen] + "…";
        return Path.Combine(parts[0], "…", parts[^2], parts[^1]);
    }
}
