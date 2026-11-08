# TestForge — Geri Bildirim Odaklı İteratif Test Ajanı

> Teknik referans. Aynı içeriğin ekip için okunabilir sürümü: [TestForge.html](TestForge.html).

## 1. Ne yapar?

TestForge, bir kaynak dosya için **birim testlerini kendisi yazan, çalıştıran ve sonuca bakarak düzelten** bir LLM ajanıdır.
Tek seferlik "bana test yaz" isteğinin aksine, test üretimini iteratif bir sürece çevirir:

```
Generate → Execute → Observe → Improve → Repeat
```

1. Kaynak dosyayı okur ve sıfır-atış (zero-shot) bir test suite yazar.
2. Testleri gerçekten çalıştırır (`dotnet test` + coverlet).
3. İki geri bildirim alır:
   - **Execution feedback:** derleme hataları, kırmızı testler, assertion mesajları ("Expected: 90, Actual: 80").
   - **Coverage feedback:** kaynak dosyanın satır kapsamı ve **kapsanmayan satırların tamamı**.
4. Her anlamlı geri bildirimden sonra kendi sonucu üzerine düşünür (self-reflection), testleri düzeltir, eksik satırlar için yeni test ekler.
5. Testler yeşil ve kapsam yeterliyse `finish` önerir. TestForge bunu doğrular; doğrulama geçmezse ajan devam eder.

Çıktı: test projesine yazılmış test dosyaları ve `<repo>/.testforge/report.json` + `report.md` raporu.

## 2. Neden yaptık?

Copilot gibi araçlar düzgün *görünen* testler üretir ama:

- Üretilen test derlenmeyebilir ya da çalışırken patlayabilir.
- Önemli branch'ler test edilmemiş olabilir; coverage düşük kalır.
- **Model kendi çıktısının sonucunu görmez.**

TestForge yaklaşımı (Jain & Le Goues, *TestForge: Feedback-Driven, Agentic Test Suite Generation*, arXiv:2503.14713) bu boşluğu kapatır:
üretilen test nihai sonuç değildir; çalıştırılır ve sonucundan öğrenilir. Makalede bu yaklaşım TestGenEval üzerinde %84,3 pass@1,
%44,4 line coverage ve %33,8 mutation score ile tek iterasyonlu LLM ve klasik (Pynguin/CodaMosa) yaklaşımları geçiyor; dosya başına
maliyet ≈ $0,63. Bu repo, makaledeki fikri şirketin **Azure OpenAI** hesabıyla ve **C#/.NET** projeleri için çalışan bir araca dönüştürür.

Makaleden korunan tasarım kararları:

| Karar | Neden |
|---|---|
| Zero-shot başlangıç | Ablation'da sıfırdan başlamaktan daha iyi; okunabilir bir iskelet verir. |
| Tek ajan, çok araç | 5 farklı ajan yok; tek ajan görevine göre araç seçer. |
| Tüm eksik satırlar ajana verilir | Ajan bir iterasyonda birden çok test planlayabilir → daha az iterasyon, daha düşük maliyet. |
| Self-reflection | "Neyi yanlış yaptım, neresi eksik?" sorusu bir sonraki adımı planlatır. |
| Dosya seviyesinde çalışma | Metot metot ilerlemekten çok daha ucuz. |
| En fazla 25 iterasyon | Makaledeki bütçe. |

## 3. Mimari

```
TestForge.slnx
src/
  TestForge.Core/              dilden bağımsız çekirdek (Azure referansı YOK)
    Llm/                       ILlmClient + kendi DTO'larımız (LlmMessage, LlmToolCall, LlmResponse, LlmUsage)
    Agent/                     TestForgeAgent (döngü), Prompts, IterationRecord, AgentOutcome
    Tools/                     7 araç + ToolRegistry
    Workspace/                 Sandbox (okuma/yazma kapsamı), ProcessRunner, OutputTrimmer
    Adapters/                  ILanguageAdapter, ITestProjectLocator
    Feedback/                  BuildResult, TestRunResult, CoverageReport, MutationReport (v2)
    Reporting/                 RunReport şeması, JSON + Markdown yazıcı
    TestForgeRunner.cs         orchestrator
  TestForge.Llm.Azure/         AzureLlmClient — Azure.AI.OpenAI yalnızca burada
  TestForge.Adapters.CSharp/   CSharpAdapter, CoberturaParser, TrxParser, TestProjectLocator
  TestForge.Cli/               `testforge` komutu
samples/                       DiscountCalculator (sunumdaki örnek) + boş test projesi
tests/TestForge.Tests/         84 test (sahte LLM ile deterministik + gerçek dotnet ile entegrasyon)
```

**Katmanlar ve sorumluluklar**

- **Orchestrator (`TestForgeRunner`)**: test projesini bulur, sandbox'ı kurar, eski test dosyasını yedekler, ajanı çalıştırır,
  ajan durduktan sonra testleri **son bir kez** koşar ve raporu yazar. Ajan "ne zaman bittiğine" karar verir; orchestrator
  "sonunda gerçekte ne durumda olduğunu" kaydeder.
- **Ajan (`TestForgeAgent`)**: LLM ↔ araç döngüsü, reflection, finish doğrulaması, stagnation tespiti.
- **Adapter (`ILanguageAdapter`)**: dile özgü build/test/coverage. Yeni dil = yeni adapter; çekirdek değişmez.
- **LLM (`ILlmClient`)**: sağlayıcıdan bağımsız arayüz. `AzureLlmClient` Azure SDK tiplerini kendi DTO'larımıza çevirir; testlerde
  `ScriptedLlmClient` kullanılır. Bu sayede Azure paketi çekirdeğe ve testlere sızmaz.

```csharp
public interface ILanguageAdapter
{
    string Name { get; }
    string FrameworkHint { get; }                       // prompt'a girer
    bool CanHandle(string repoRoot, string sourceFile);
    string GetTestFilePath(string repoRoot, string sourceFile, string testProject); // zero-shot için önerilen ilk yol
    Task<BuildResult> BuildAsync(string repoRoot, string testProject, CancellationToken ct);   // sadece edit_file çağırır
    Task<TestRunResult> RunTestsAsync(string repoRoot, string testProject, string sourceFile, CancellationToken ct);
    Task<MutationReport?> RunMutationAsync(...) => Task.FromResult<MutationReport?>(null); // v1: yok
}
```

`RunTestsAsync` tek bir `dotnet test --collect:"XPlat Code Coverage" --logger trx` çağrısıyla hem sonuçları (TRX) hem
coverage'ı (Cobertura) toplar. Coverage her zaman **kaynak dosyaya** göre ölçülür; ajanın kaç test dosyası yazdığı önemli değildir.

## 4. Ajan döngüsü

```
orchestrator: test projesini bul → sandbox kur → eski test dosyasını .testforge/backup'a taşı
   │
   ▼
[system prompt + zero-shot prompt (kaynak kodun tamamı)]
   │
   ▼  iterasyon = 1 LLM çağrısı + istediği tüm tool call'lar   (en fazla 25)
LLM ──tool calls──▶ araçlar ──sonuç──▶ mesaj geçmişi
   ▲                                       │
   └──── reflection (yalnızca anlamlı geri bildirimden sonra) ◀┘
   │
   ├─ finish kabul      → dur (finish)
   ├─ stagnation        → dur (stagnation)
   └─ 25 iterasyon      → dur (max_iterations)
   │
   ▼
orchestrator: son test koşusu → report.json + report.md
```

### 4.1 Reflection — ne zaman eklenir?

Tek bir `ReflectionTemplate` vardır ("Ne başarısız oldu ve neden? Hangi satırlar hâlâ kapsanmıyor? Sıradaki somut planın ne?").
Gürültüyü ve maliyeti düşürmek için yalnızca anlamlı geri bildirimden sonra eklenir; bir iterasyonda en fazla bir kez:

| Çağrı | Reflection |
|---|---|
| `run_tests` | **Her zaman** |
| `edit_file`, build hatası varsa | **Evet** |
| `finish` reddedildiyse | **Evet** (red metniyle birlikte) |
| `view_file`, `search_file`, `write_file`, `execute_bash`, başarılı `edit_file` | Hayır |

### 4.2 Finish doğrulaması

`finish(reason)` bir öneridir. Ajanın içinde testler son bir kez çalıştırılır ve şu koşul aranır:

```
BuildSucceeded && Failed == 0 && Passed > 0
```

- Kırmızı / derlenmeyen: `finish reddedildi: testler hâlâ kırmızı (X failed, Y compile error). Önce hataları düzelt, sonra finish çağır.`
- Hiç test yok ya da hepsi skip: `finish reddedildi: hiç test çalışmadı (Passed == 0). Önce en az bir test yaz ve geçtiğinden emin ol.`
- Red **bir iterasyon tüketir** (25'e sayılır) ve stagnation koşullarını **sıfırlamaz**.
- Ajanın gerekçesi (`reason`) rapora "Ajanın bitiş gerekçesi" olarak yazılır.

### 4.3 Stagnation

Her iterasyon sonunda, son 3 iterasyona bakılır. İki bağımsız koşul vardır; biri doğruysa döngü `stagnation` ile kapanır:

- **(a)** Son 3 iterasyonun hiçbirinde dosya değişmedi, her birinde test koşuldu ve tüm sonuçlarda `(failed, coverage%)` aynı.
  → "Hiçbir şey değiştirmeden aynı sonucu tekrar tekrar görüyor."
- **(b)** Son 3 iterasyonun hiçbirinde ne `run_tests` ne de dosya değişikliği var (ör. sürekli `view_file`/`search_file`).

Notlar:
- Test eklendiği halde coverage'ın sabit kalması stagnation **değildir** (ajan ilerliyor olabilir).
- "Dosya değişikliği" = **başarılı** `write_file`/`edit_file`. Başarısız bir edit (eşleşme yok) dosyayı değiştirmediği için ilerleme sayılmaz;
  aksi halde ajan aynı hatalı edit'i 25 kez deneyebilirdi.
- `finish` doğrulamasındaki test koşusu (a) için sonuç olarak sayılır ama `HasRunTests` bayrağını set etmez; bu yüzden
  üst üste 3 finish reddi (b) ile stagnation'a düşer.

## 5. Araçlar ve kurallar

Ajana sunulan araçlar **yalnızca** bunlardır. Build, coverage ve mutation için ayrı araç **yoktur**.

| Araç | Görevi | Kurallar |
|---|---|---|
| `view_file(path, start_line?)` | Dosyayı satır numaralı okur | En fazla 400 satırlık pencere; repo kökü içinde |
| `search_file(name_pattern?, contains_text?, directory?)` | Dosya adı (wildcard: `Discount*`, `*Tests.cs`) ve içerik araması | En fazla 100 sonuç; `bin/obj/.git/.testforge` atlanır |
| `write_file(path, content)` | **Yeni** dosya oluşturur | Dosya varsa reddedilir → `edit_file` kullan. Yalnızca test projesi altında |
| `edit_file(path, mode, ...)` | `replace(old_text, new_text)` veya `insert_after(anchor, text)` | Metin **tam bir kez** geçmeli (0 ya da 1'den fazla eşleşme → açıklayıcı hata). Başarıdan sonra test projesi **otomatik derlenir**, sonuç çıktıya eklenir. Yalnızca test projesi altında |
| `execute_bash(command)` | Salt-okunur keşif | Allowlist: `ls`, `dir`, `cat`, `type`, `dotnet --info`, `dotnet --list-sdks`, `dotnet sln list`. `; & \| > < \` $` yasak. Dosya argümanları repo içinde olmalı |
| `run_tests()` | Build + test + coverage | Testleri çalıştırmanın **tek** yolu. Passed/failed/skipped, derleme hataları, hata mesajları, coverage % ve **tüm** eksik satırlar (aralık olarak, hiçbiri atlanmadan: `3-5, 9, 12-13`) |
| `finish(reason)` | Bitirme önerisi | Bkz. 4.2 |

**Ret mesajları (ajan bunları tool sonucu olarak görür):**

- `dotnet build` → *Derleme edit_file sonrası otomatik yapılır; ayrıca build çağırma.*
- `dotnet test` / `dotnet run` / `dotnet-stryker` → *Test için run_tests kullan.*
- Kaynak dosyaya yazma → *Yazma yalnızca test projesi altında (...) yapılabilir. Kaynak dosyalar düzenlenemez.*
- Var olan dosyaya `write_file` → *'...' zaten var. Mevcut dosyayı değiştirmek için edit_file kullan...*

**Neden bu kurallar?**

1. **`run_tests` tek yol:** Ajan `execute_bash` ile `dotnet test` çalıştırsaydı coverage toplanmaz, çıktı yapılandırılmamış olurdu.
2. **Build ayrı araç değil:** Ajan "build et → test et" diye iki adım harcamasın; `edit_file` zaten derleme sonucunu döndürür.
3. **`write_file` kilitli:** LLM'ler her iterasyonda tüm dosyayı yeniden yazmaya eğilimlidir; bu diff'leri okunamaz yapar ve ajanın
   "ne değişti" farkındalığını bozar.
4. **WriteScope:** Ajan test yazmakla görevlidir; kaynak kodu "testi geçirmek için" değiştirmesi en tehlikeli hatadır. Sandbox bunu
   araç seviyesinde imkânsız kılar.
5. **Finish doğrulaması:** "Pes edip finish" ya da boş dosyayla finish engellenir.

Uzun çıktılar baş + son kısmı korunarak kırpılır (≈12k karakter); eksik satır listesi her zaman tamdır.

## 6. Kurulum

Gereksinimler: .NET 10 SDK, test edilecek projede bir xUnit test projesi (`*.Tests.csproj`, `coverlet.collector` paketiyle —
`dotnet new xunit` şablonu bunu zaten içerir) ve bir Azure OpenAI deployment'ı (tool calling destekleyen bir model, ör. gpt-4o / gpt-4.1).

```bash
dotnet build TestForge.slnx
```

```bash
dotnet test tests/TestForge.Tests
```

Sadece hızlı birim testleri için (gerçek `dotnet test` çağıran entegrasyon testlerini atlayarak):

```bash
dotnet test tests/TestForge.Tests --filter "Category!=Integration"
```

## 7. Azure OpenAI ayarı

Üç değer gerekir: endpoint, API anahtarı ve deployment adı. Öncelik sırası: **ortam değişkeni > user-secrets > `./testforge.json`**.
Anahtarı asla repoya yazmayın (`testforge.json` `.gitignore`'dadır).

**Seçenek 1 — ortam değişkenleri (PowerShell):**

```powershell
$env:AZURE_OPENAI_ENDPOINT   = "https://<kaynak-adi>.openai.azure.com/"
$env:AZURE_OPENAI_API_KEY    = "<anahtar>"
$env:AZURE_OPENAI_DEPLOYMENT = "<deployment-adi>"
```

**Seçenek 2 — dotnet user-secrets (anahtar kullanıcı profilinde saklanır):**

```bash
dotnet user-secrets --project src/TestForge.Cli set "AzureOpenAI:Endpoint" "https://<kaynak-adi>.openai.azure.com/"
```

```bash
dotnet user-secrets --project src/TestForge.Cli set "AzureOpenAI:ApiKey" "<anahtar>"
```

```bash
dotnet user-secrets --project src/TestForge.Cli set "AzureOpenAI:Deployment" "<deployment-adi>"
```

**Seçenek 3 — `testforge.json`** (komutu çalıştırdığınız klasörde; şablon: `testforge.example.json`).

Eksik bir değer varsa CLI hangi değerin eksik olduğunu söyler ve `2` koduyla çıkar.
Azure AI Foundry endpoint'leri de aynı istemciyle çalışır; endpoint'i olduğu gibi verin.

## 8. Kullanım

```
testforge generate --repo <yol> --file <kaynak> [--test-project <csproj>] [--max-iter 25]
```

| Seçenek | Açıklama |
|---|---|
| `--repo` | Test edilecek kodun repository kökü. Ajan yalnızca bunun altını okuyabilir. |
| `--file` | Test üretilecek kaynak dosya (repo'ya göre göreli ya da mutlak). |
| `--test-project` | Opsiyonel. Verilmezse `*.Tests.csproj` aranır: tek aday varsa o; birden fazlaysa kaynak dosyanın projesiyle eşleşen (`X.csproj` → `X.Tests.csproj`). Bulunamaz ya da belirsizse: *Test projesi bulunamadı. --test-project <yol> verin veya repo altına bir \*.Tests.csproj ekleyin.* |
| `--max-iter` | İterasyon bütçesi (1–100, varsayılan 25). |

Örnek (repo içindeki örnek proje):

```bash
dotnet run --project src/TestForge.Cli -- generate --repo samples --file DiscountCalculator/DiscountService.cs
```

Örnek konsol çıktısı (kısaltılmış):

```
[testforge] Kaynak:       DiscountCalculator/DiscountService.cs
[testforge] Test projesi: DiscountCalculator.Tests/DiscountCalculator.Tests.csproj
[testforge] Test dosyası: DiscountCalculator.Tests/DiscountServiceTests.cs
[testforge] Ajan başladı (model: gpt-4o, en fazla 25 iterasyon)...
[testforge] Ajan durdu: finish
[testforge] Son test koşusu...

Sonuç: finish | passed 14 | failed 0 | coverage %100.0 | token 48210
Rapor: samples/.testforge/report.md
```

Çıkış kodları: `0` finish kabul edildi · `3` ajan finish'e ulaşmadan durdu (stagnation / max_iterations / error; rapor yine yazılır) ·
`2` kullanım ya da ayar hatası · `130` Ctrl+C.

**Var olan testler:** Zero-shot adımının `write_file` ile yazabilmesi için önerilen test dosyası (ör. `DiscountServiceTests.cs`) ve şablonun
`UnitTest1.cs` dosyası varsa **silinmez**, `.testforge/backup/<zaman>/` altına taşınır. Diğer test dosyalarına dokunulmaz.

## 9. Rapor

`<repo>/.testforge/report.json` (şema v1):

```json
{
  "meta": { "startedAt": "ISO-8601", "finishedAt": "ISO-8601", "repoRoot": "", "sourceFile": "",
            "testProject": "", "adapter": "csharp", "model": "deployment", "maxIterations": 25 },
  "outcome": { "stopReason": "finish|stagnation|max_iterations|error", "stopDetail": "", "finishReason": null,
               "finalPassed": 0, "finalFailed": 0, "finalCoveragePercent": 0.0, "finalBuildSucceeded": true },
  "iterations": [ { "index": 1, "passed": 0, "failed": 0, "coveragePercent": 0.0,
                    "tokensUsedThisIteration": 0, "cumulativeTokens": 0, "toolCallsCount": 2,
                    "toolCalls": [ { "name": "edit_file", "path": "DiscountCalculator.Tests/DiscountServiceTests.cs" },
                                   { "name": "run_tests" } ],
                    "reflectionAdded": true, "timestamp": "ISO-8601" } ],
  "usage": { "promptTokens": 0, "completionTokens": 0, "totalTokens": 0 },
  "mutation": null
}
```

- `final*` alanları orchestrator'ın **son koşusundan** gelir (ajanın son gördüğünden değil).
- Test koşulmayan iterasyonlar son bilinen passed/failed/coverage değerini taşır → coverage eğrisi kesintisizdir.
- `toolCalls[].path` yalnızca `edit_file` ve `write_file` için yazılır.
- `report.md`: başlık, özet tablosu, coverage gidişi (`%65 → %82 → %100`), iterasyon tablosu
  (`| # | passed | failed | coverage% | tokens | toolCalls |`) ve ajanın bitiş gerekçesi.

## 10. Testler

`tests/TestForge.Tests` (84 test):

- **Araçlar:** allowlist ve ret mesajları, zincir operatörleri, write kilidi, WriteScope, edit eşleşme kuralları, edit → build, satır sonları.
- **Ajan (`ScriptedLlmClient` ile):** mutlu yol (zero-shot → kırmızı → düzeltme → yeşil → finish), reflection tetikleyicileri,
  finish red/kabul senaryoları, stagnation (a)/(b), max_iterations.
- **Adapter:** Cobertura (aynı dosyanın birden çok class'ı birleştirilir), TRX, derleme hatası ayrıştırma, test projesi keşfi.
- **Rapor:** JSON şema alanları, `path` kuralı, son bilinen değer taşıma, Markdown.
- **Entegrasyon (`Category=Integration`):** örnek projenin geçici bir kopyasında **gerçek** `dotnet test` + coverlet ile tam döngü:
  kasıtlı yanlış assertion'ı görür, düzeltir, negatif fiyat testini ekler, coverage artar, finish kabul edilir, rapor yazılır.

`ScriptedLlmClient` her çağrıda gelen mesajların derin kopyasını saklar ve `AssertReflectionAddedAfter`, `AssertNoReflectionAfter`,
`AssertToolResultContains` yardımcılarını sunar; senaryolar tamamen deterministiktir ve Azure anahtarı gerektirmez.

## 11. Sınırlar ve v2 yol haritası

**v1 sınırları**

- Yalnızca C# / xUnit + coverlet. Coverage satır bazlıdır (branch coverage rapora girmez).
- Komutlar Docker içinde değil, yerel makinede çalışır. `execute_bash` allowlist ile sınırlı olsa da üretilen **testlerin kendisi** yerel
  makinede çalıştırılır; güvenilmeyen kodu bu araçla test etmeyin.
- Ajan kaynak koddaki bir hatayı düzeltmez; mevcut davranışı test eder ve bunu bitiş gerekçesinde belirtebilir.
- Bağlam penceresi: çok uzun konuşmalarda eski mesajlar kırpılmaz; tool çıktıları kırpılır. Rapordaki token sütunu bunu izlemek içindir.

**v2**

- **Mutation testing:** "%100 coverage ≠ %100 doğru test". Coverage yalnızca satırdan geçildiğini söyler; assertion'ın doğru olduğunu
  garanti etmez. Plan: Stryker.NET (`dotnet-stryker`) ile `RunMutationAsync`'i doldurmak, `run_mutation` aracını kaydetmek ve
  hayatta kalan mutantları ajana geri bildirim olarak vermek. Arayüz ve `MutationReport` tipi hazır; rapordaki `mutation` alanı şimdiden yerinde.
- **Python / JS adapter'ları:** pytest + coverage.py (+ mutmut), vitest/jest + c8 (+ Stryker JS). Çekirdek değişmeden `ILanguageAdapter` ile eklenir.
- **Docker sandbox:** makaledeki gibi tüm komutları izole bir konteynerde çalıştırmak.
- **Toplu mod:** bir klasördeki tüm dosyalar için sırayla çalışıp tek bir özet rapor üretmek.
