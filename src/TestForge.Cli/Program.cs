using System.Globalization;
using Microsoft.Extensions.Configuration;
using TestForge.Adapters.CSharp;
using TestForge.Cli;
using TestForge.Core;
using TestForge.Core.Adapters;
using TestForge.Llm.Azure;

Console.OutputEncoding = System.Text.Encoding.UTF8;

CliArguments cli;
try
{
    cli = CliArguments.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"Hata: {ex.Message}\n");
    Console.Error.WriteLine(CliArguments.Usage);
    return 2;
}

if (cli.ShowHelp)
{
    Console.WriteLine(CliArguments.Usage);
    return 0;
}

AzureLlmOptions azureOptions;
try
{
    azureOptions = LoadAzureOptions();
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"Hata: {ex.Message}");
    return 2;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var runner = new TestForgeRunner(
    new AzureLlmClient(azureOptions),
    [new CSharpAdapter()],
    new TestProjectLocator(),
    message => Console.WriteLine($"[testforge] {message}"));

try
{
    var result = await runner.RunAsync(new RunOptions(cli.Repo!, cli.File!, cli.TestProject, cli.MaxIterations), cts.Token);
    var o = result.Report.Outcome;
    Console.WriteLine();
    Console.WriteLine($"Sonuç: {o.StopReason} | passed {o.FinalPassed} | failed {o.FinalFailed} | coverage %{o.FinalCoveragePercent.ToString("0.0", CultureInfo.InvariantCulture)} | token {result.Report.Usage.TotalTokens}");
    Console.WriteLine($"Rapor: {Path.Combine(result.ReportDirectory, "report.md")}");
    return o.StopReason == "finish" ? 0 : 3;
}
catch (TestProjectNotFoundException ex)
{
    Console.Error.WriteLine($"Hata: {ex.Message}");
    return 2;
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"Hata: {ex.Message}");
    return 2;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("İptal edildi.");
    return 130;
}

static AzureLlmOptions LoadAzureOptions()
{
    // Precedence: environment variables > user-secrets > testforge.json (current directory).
    var config = new ConfigurationBuilder()
        .AddJsonFile(Path.Combine(Directory.GetCurrentDirectory(), "testforge.json"), optional: true)
        .AddUserSecrets(typeof(CliArguments).Assembly, optional: true)
        .Build();

    string? Read(string env, string key) =>
        Environment.GetEnvironmentVariable(env) is { Length: > 0 } v ? v : config[key];

    return AzureLlmOptions.FromValues(
        Read("AZURE_OPENAI_ENDPOINT", "AzureOpenAI:Endpoint"),
        Read("AZURE_OPENAI_API_KEY", "AzureOpenAI:ApiKey"),
        Read("AZURE_OPENAI_DEPLOYMENT", "AzureOpenAI:Deployment"));
}
