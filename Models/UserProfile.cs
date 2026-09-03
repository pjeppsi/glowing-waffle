namespace BenchmarkApp.Models;

// Kanonski oblik odgovora za Q1 (profil po ID-u).
// I Neo4jQueries.cs I MongoQueries.cs MORAJU vratiti TOČNO ovaj record -
// to nas prisiljava da obje baze vrate iste podatke u istom obliku,
// pa se rezultati mogu pošteno usporediti.
public record UserProfile(string Id, string Username, string Name, DateTime CreatedAt);
