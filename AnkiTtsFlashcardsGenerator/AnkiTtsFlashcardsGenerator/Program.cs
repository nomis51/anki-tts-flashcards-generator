using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Spectre.Console;

const string outputFilePath = "flashcardsWithTTS.csv";

string pathToAnkiMediaFolder;
if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    // TODO:
    pathToAnkiMediaFolder = string.Empty;
}
else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
{
    pathToAnkiMediaFolder = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile),
        ".local/share/Anki2/User 1/collection.media/"
    );
}
else
{
    AnsiConsole.MarkupLine("[red]Unsupported OS.[/]");
    return 1;
}

Console.WriteLine("Paste your flashcards CSV content then press Ctrl+D twice:");
var input = Console.In.ReadToEnd();
if (string.IsNullOrWhiteSpace(input))
{
    AnsiConsole.MarkupLine("[red]No input provided.[/]");
    return 1;
}

var lines = input.Split('\r')
    .Where(e => !string.IsNullOrWhiteSpace(e))
    .ToArray();
if (lines.Length == 0)
{
    AnsiConsole.MarkupLine("[red]No input provided.[/]");
    return 1;
}

var separator = await AnsiConsole.PromptAsync(
    new SelectionPrompt<string>()
        .Title("Choose separator: ")
        .AddChoices(";", ",", "<tab>", "<space>")
);
if (string.IsNullOrWhiteSpace(separator))
{
    AnsiConsole.MarkupLine("[red]No separator provided.[/]");
    return 1;
}

var data = lines.Select(line => line.Split(separator))
    .Where(d => d.Length == 2)
    .Where(d => !string.IsNullOrWhiteSpace(d[0]) && !string.IsNullOrWhiteSpace(d[1]));

var sideToTts = await AnsiConsole.PromptAsync(
    new SelectionPrompt<string>()
        .Title("Choose side to use for TTS: ")
        .AddChoices("Front", "Back")
);
var ttsFront = sideToTts.Equals("front", StringComparison.OrdinalIgnoreCase);

var languageCode = await AnsiConsole.PromptAsync(
    new TextPrompt<string>("Input language code (e.g. en):")
);
if (string.IsNullOrWhiteSpace(languageCode))
{
    AnsiConsole.MarkupLine("[red]No language code provided.[/]");
    return 1;
}

var httpClient = new HttpClient();
var channel = Channel.CreateBounded<string[]>(
    new BoundedChannelOptions(Environment.ProcessorCount)
    {
        FullMode = BoundedChannelFullMode.Wait,
    }
);
ConcurrentBag<string> outputLines = [];
var error = false;
var task = Task.Run(async () =>
{
    await foreach (var d in channel.Reader.ReadAllAsync())
    {
        var front = ttsFront ? d[0] : d[1];
        var back = ttsFront ? d[1] : d[0];
        var text = Uri.EscapeDataString(Regex.Replace(front, "<.*?>", string.Empty));
        using var response = await httpClient.GetAsync(
            $"https://translate.google.com/translate_tts?ie=UTF-8&client=tw-ob&q={text}&tl={languageCode}"
        );
        if (!response.IsSuccessStatusCode)
        {
            AnsiConsole.MarkupLine($"[red]Error while fetching TTS for '{text[0]}': {response.StatusCode}[/]");
            error = true;
            return;
        }

        var fileName = $"{Guid.NewGuid()}.mp3";
        var filePath = Path.Combine(pathToAnkiMediaFolder, fileName);
        await File.WriteAllBytesAsync(filePath, await response.Content.ReadAsByteArrayAsync());
        outputLines.Add($"[sound:{fileName}]{front}{separator}{back}");
    }
});

foreach (var d in data)
{
    if (error) return 1;

    await channel.Writer.WriteAsync(d);
    Console.WriteLine($"Processing '{d[0]}{separator}{d[1]}'...");
}

if (error) return 1;

channel.Writer.Complete();
await channel.Reader.Completion;
await task;

await File.WriteAllLinesAsync(outputFilePath, outputLines);

AnsiConsole.MarkupLine("[green]Done.[/]");
AnsiConsole.MarkupLine(
    $"[green]You can now import the file '{outputFilePath}' in Anki. Make sure to enable the option 'Allow HTML' in the import options.[/]");

return 0;