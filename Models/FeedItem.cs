namespace BenchmarkApp.Models;

// Kanonski oblik odgovora za Q4 (feed). Napomena: NEMA polje "Id" nego
// "PostId" - Verifier.cs mora to znati kad uspoređuje rezultate po ID-u,
// jer generička pretpostavka "svaki record ima Id" ovdje ne vrijedi.
public record FeedItem(string PostId, string AuthorId, string AuthorUsername, string Text, DateTime CreatedAt);
