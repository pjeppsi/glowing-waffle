namespace BenchmarkApp.Models;

// Sirovi red objave kakav generator upisuje u data/raw/posts.csv.
public record PostRaw(string Id, string AuthorId, string Text, DateTime CreatedAt);
