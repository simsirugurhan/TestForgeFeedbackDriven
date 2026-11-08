# TestForge

Geri bildirim odaklı (feedback-driven), iteratif birim testi üreten ajan. Ajan testleri yazar, gerçekten çalıştırır, derleme hatalarını, kırmızı testleri ve kapsanmayan satırları görür, sonra test suite'i düzeltir. Bunu testler yeşil olana ve kapsam yeterli olana kadar tekrarlar. Arkada Azure OpenAI ve C#/.NET (xUnit + coverlet) kullanılır.

- **Ekip için rehber:** [docs/TestForge.html](docs/TestForge.html)
- **Teknik referans:** [docs/TestForge.md](docs/TestForge.md)

## Hızlı başlangıç

```bash
dotnet build TestForge.slnx
```

```powershell
$env:AZURE_OPENAI_ENDPOINT   = "https://<kaynak-adi>.openai.azure.com/"
$env:AZURE_OPENAI_API_KEY    = "<anahtar>"
$env:AZURE_OPENAI_DEPLOYMENT = "<deployment-adi>"
```

```bash
dotnet run --project src/TestForge.Cli -- generate --repo samples --file DiscountCalculator/DiscountService.cs
```

Rapor `samples/.testforge/report.md` dosyasına yazılır.

## Testler

```bash
dotnet test tests/TestForge.Tests
```

Testler sahte bir LLM ile çalışır, Azure anahtarı gerektirmez. Bunlara gerçek `dotnet test` çalıştıran entegrasyon testleri de dahildir.
