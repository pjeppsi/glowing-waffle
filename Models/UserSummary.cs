namespace BenchmarkApp.Models;

// Kanonski oblik odgovora za Q2 (pratitelji) i Q3 (prijatelji prijatelja).
// Namjerno bez CreatedAt - ta polja upiti Q2/Q3 ne trebaju, pa ih ne vraćamo
// (manje polja u shape-u = manje toga za mapirati i uspoređivati).
public record UserSummary(string Id, string Username, string Name);
