namespace BenchmarkApp.Models;

// Sirovi red korisnika kakav generator upisuje u data/raw/users.csv.
// Ovo NIJE isto što i UserProfile/UserSummary (koji su kanonski oblik
// ODGOVORA upita) - ovo je oblik ULAZNIH podataka prije nego uopće
// dođu u bilo koju bazu.
public record UserRaw(string Id, string Username, string Name, DateTime CreatedAt);
