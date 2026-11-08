namespace TestForge.Cli;

public sealed record CliArguments(string? Repo, string? File, string? TestProject, int MaxIterations, bool ShowHelp)
{
    public const string Usage = """
        TestForge — geri bildirim odaklı iteratif test ajanı

        Kullanım:
          testforge generate --repo <yol> --file <kaynak> [--test-project <csproj>] [--max-iter 25]

        Seçenekler:
          --repo           Test edilecek kodun bulunduğu repository kökü.
          --file           Test üretilecek kaynak dosya (repo'ya göre göreli ya da mutlak).
          --test-project   Test projesi (.csproj). Verilmezse *.Tests.csproj otomatik aranır.
          --max-iter       En fazla iterasyon (varsayılan 25).
          -h, --help       Bu yardımı gösterir.

        Azure OpenAI ayarları (ortam değişkeni > user-secrets > ./testforge.json):
          AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT
        """;

    public static CliArguments Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args.Any(a => a is "-h" or "--help" or "help"))
            return new CliArguments(null, null, null, 25, ShowHelp: true);

        if (args[0] != "generate")
            throw new ArgumentException($"Bilinmeyen komut '{args[0]}'. Şu an yalnızca 'generate' destekleniyor.");

        string? repo = null, file = null, testProject = null;
        var maxIter = 25;
        for (var i = 1; i < args.Count; i++)
        {
            string Value() => i + 1 < args.Count ? args[++i] : throw new ArgumentException($"{args[i]} için değer eksik.");
            switch (args[i])
            {
                case "--repo": repo = Value(); break;
                case "--file": file = Value(); break;
                case "--test-project": testProject = Value(); break;
                case "--max-iter":
                    if (!int.TryParse(Value(), out maxIter) || maxIter < 1 || maxIter > 100)
                        throw new ArgumentException("--max-iter 1 ile 100 arasında bir sayı olmalı.");
                    break;
                case "--mutation":
                    throw new ArgumentException("--mutation v1'de desteklenmiyor (v2 yol haritasında).");
                default:
                    throw new ArgumentException($"Bilinmeyen seçenek '{args[i]}'.");
            }
        }

        if (repo is null) throw new ArgumentException("--repo zorunlu.");
        if (file is null) throw new ArgumentException("--file zorunlu.");
        return new CliArguments(repo, file, testProject, maxIter, ShowHelp: false);
    }
}
