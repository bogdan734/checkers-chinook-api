// Runs the spec's acceptance criteria against a live instance:
//   dotnet run --project tools/AcceptanceCheck -c Release -- http://localhost:5000 [--tb-pdn "<pdn>"] [--midgame-pdn "<pdn>"]
// Exit code 0 when every check passes, 1 otherwise.
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

string Opt(string name, string fallback)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

string baseUrl = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "http://localhost:5000";
// Default tablebase position has 3 pieces, which the built-in engine covers. With the real Chinook databases pass an 8-piece one.
string tbPdn = Opt("--tb-pdn", "W:WK14,K17:BK5");
string midgamePdn = Opt("--midgame-pdn", "B:W18,19,22,25,27,28,30,32:B1,5,6,7,10,12,14,16");
int tbMaxMs = int.Parse(Opt("--tb-max-ms", "50"));
int strongMaxMs = int.Parse(Opt("--strong-max-ms", "600"));

using var http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(30) };

async Task<(HttpStatusCode Status, JsonElement Body, long Ms)> Post(string path, object payload)
{
    var sw = Stopwatch.StartNew();
    using var res = await http.PostAsync(path, new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
    string text = await res.Content.ReadAsStringAsync();
    sw.Stop();
    return (res.StatusCode, JsonDocument.Parse(text).RootElement.Clone(), sw.ElapsedMilliseconds);
}

object Suggest(string pdn, string level, object? limits = null) =>
    new { gameId = "acceptance", state = new { notation = "PDN", position = pdn }, level, limits };

var results = new List<(bool Ok, string Name, string Detail)>();
void Add(bool ok, string name, string detail) => results.Add((ok, name, detail));

try
{
    using var health = await http.GetAsync("healthz");
    var h = JsonDocument.Parse(await health.Content.ReadAsStringAsync()).RootElement;
    Add(health.IsSuccessStatusCode && h.GetProperty("ok").GetBoolean(), "healthz is ok on startup", $"HTTP {(int)health.StatusCode}, workers={h.GetProperty("workers").GetInt32()}");

    await Post("v1/move/suggest", Suggest(tbPdn, "strong"));                        // warm-up, fills the cache for this key
    var (tbStatus, tb, tbMs) = await Post("v1/move/suggest", Suggest(tbPdn, "strong", new { maxDepth = 17 })); // different key, so no cache hit
    bool tbHit = tbStatus == HttpStatusCode.OK && tb.GetProperty("info").GetProperty("tablebaseHit").GetBoolean();
    Add(tbHit && tbMs < tbMaxMs, $"tablebase position answers in < {tbMaxMs} ms with tablebaseHit", $"{tbMs} ms, tablebaseHit={tbHit}");

    await Post("v1/move/suggest", Suggest(midgamePdn, "weak"));                     // warm-up on another level
    var (midStatus, mid, midMs) = await Post("v1/move/suggest", Suggest(midgamePdn, "strong"));
    string best = midStatus == HttpStatusCode.OK ? mid.GetProperty("bestMove").GetString()! : "";
    var (_, valid, _) = await Post("v1/move/validate", new { position = midgamePdn, move = best });
    bool legal = valid.TryGetProperty("legal", out var l) && l.GetBoolean();
    string detail = midStatus == HttpStatusCode.OK
        ? $"{best} in {midMs} ms, depth {mid.GetProperty("depth").GetInt32()}, nodes {mid.GetProperty("nodes").GetInt64()}"
        : $"HTTP {(int)midStatus}";
    Add(midStatus == HttpStatusCode.OK && midMs < strongMaxMs && legal, $"strong midgame answers in < {strongMaxMs} ms with a legal move", detail);

    var (badStatus, _, _) = await Post("v1/move/suggest", Suggest("B:W99:B1", "weak"));
    Add((int)badStatus == 422, "invalid PDN returns 422", $"HTTP {(int)badStatus}");

    var (slowStatus, _, slowMs) = await Post("v1/move/suggest", Suggest(midgamePdn, "strong", new { maxDepth = 40, softTimeMs = 5000, hardTimeMs = 30 }));
    Add((int)slowStatus == 504, "hardTimeMs expiry returns 504", $"HTTP {(int)slowStatus} after {slowMs} ms");
}
catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
{
    Add(false, "service reachable", $"{baseUrl}: {e.Message}");
}

foreach (var (ok, name, detail) in results)
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  ({detail})");
Console.WriteLine(results.All(r => r.Ok) ? "\nAll acceptance criteria passed." : "\nSome acceptance criteria failed.");
return results.All(r => r.Ok) ? 0 : 1;
