namespace BenchmarkApp.Models;

// Jedan red iz data/raw/pairs.csv - par korisnika za Q5 (najkraći put),
// za koji je generator VEĆ provjerio usmjerenim BFS-om da put postoji
// i da nije duži od max_hops. ExpectedLength čuvamo da runner ima s čime
// usporediti stvarni rezultat upita (iako to Verifier ne radi eksplicitno -
// Verifier uspoređuje Neo4j i Mongo MEĐUSOBNO, ne prema ExpectedLength;
// ExpectedLength je ipak koristan za brzu ručnu provjeru razumnosti).
public record PairRaw(string FromId, string ToId, int ExpectedLength);
