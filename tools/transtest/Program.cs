using System;
using System.Threading;
using System.Threading.Tasks;
using LlmTranslate;

string sample = """
# Measurement Report

The net count rate is defined below. See the variable $x_i$ and the value `n_g`.

$$
u(y) = \sqrt{w^2 u^2(n) + n^2 u^2(w)}
$$

| Name | Value |
| --- | --- |
| Mass | 12.5 |
| Length | 3.2 |
""";

var cfg = new AppConfig();
if (args.Length > 2 && int.TryParse(args[2], out var mc)) cfg.MaxCharsPerChunk = mc; // force multi-chunk for tests
using var client = new OpenAiClient(cfg);

var model = await client.GetModelAsync();
Console.WriteLine("model: " + (model ?? "<offline>"));
if (model == null)
{
    Console.WriteLine("Server offline — skipping live translation.");
    return;
}

var tr = new Translator(cfg, client, s => Console.WriteLine("  " + s));
var progress = new Progress<double>(p => Console.WriteLine($"  progress {p:P0}"));
string? progressDir = args.Length > 0 ? args[0] : null;   // pass a dir to test checkpoint/resume
int gpu = args.Length > 1 && int.TryParse(args[1], out var g) ? g : 100;
string ru = await tr.TranslateDocumentAsync(sample, progressDir, () => gpu, progress, CancellationToken.None);

Console.WriteLine("=== RESULT ===");
Console.WriteLine(ru);
