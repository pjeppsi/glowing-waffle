namespace BenchmarkApp.Web;

// Ulazni/izlazni oblici za /api/* rute (Faza 4 plana). Cisti podatkovni
// recordi, nema logike - logika izvrsavanja je u Endpoints.cs, koji poziva
// ISKLJUCIVO Queries/* (nema duplikata upita ovdje).

public record RunRequest(string Query, string? Subject, string? FromId, string? ToId);

public record RunVariantResult(string Name, double ElapsedMs, int RowCount, object? Rows, string QueryText, string? Note);

public record RunResponse(List<RunVariantResult> Variants, bool AllMatch);

public record BenchRequest(string Query, string? Subject, string? FromId, string? ToId, int Iterations);

public record BenchVariantResult(string Name, double Median, double Min, double P95, double Max);

public record BenchResponse(List<BenchVariantResult> Variants, string Warning);

public record RawRequest(string Engine, string Text);

public record RawResponse(bool Ok, object? Rows, int RowCount, string? Error);

public record SubjectsResponse(List<string> Subjects, List<PairDto> Pairs);

public record PairDto(string FromId, string ToId, int ExpectedLength);
