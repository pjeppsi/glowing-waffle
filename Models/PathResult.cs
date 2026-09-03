namespace BenchmarkApp.Models;

// Kanonski oblik odgovora za Q5 (najkraći put).
// Path je lista ID-eva čvorova od polazišta do cilja (uključivo oba kraja).
// Length je broj hopova (bridova), tj. Path.Count - 1.
// Ako put ne postoji unutar dozvoljene granice (max_hops), Path je prazna
// lista, a Length je -1 (dogovorena oznaka "nema puta", ne 0 jer bi 0
// hopova moglo pobrkati sa slučajem "isti korisnik kao i on sam").
public record PathResult(List<string> Path, int Length);
