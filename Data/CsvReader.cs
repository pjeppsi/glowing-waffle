using System.Globalization;

namespace BenchmarkApp.Data;

// Sitan zajednicki helper za citanje CSV-ova koje je napisao nas vlastiti
// Generator.cs. NIJE opcenita CSV biblioteka - nema escapinga navodnika/
// zareza unutar polja, jer Generator.cs GARANTIRA da izvorni podaci (imena,
// recenice objava) nikad ne sadrze zarez ni navodnik (v. komentar u
// Generator.cs kod WriteUsersCsv). Zato je ovdje dovoljan goli Split(',').
public static class CsvReader
{
    // Cita sve retke osim zaglavlja (prvi red), vraca ih kao string[] polja
    // podijeljena po zarezu - pozivatelj sam mapira polja u svoj record po
    // indeksu, jer svaki CSV ima drukciji broj/tip stupaca.
    public static IEnumerable<string[]> ReadRows(string path)
    {
        using var reader = new StreamReader(path);

        string? header = reader.ReadLine(); // zaglavlje se preskace, ne treba nam u kodu
        if (header is null)
        {
            yield break; // prazna datoteka - nema redaka
        }

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
            {
                continue; // preskoci prazan redak na kraju datoteke (cest slucaj)
            }

            yield return line.Split(',');
        }
    }

    // Generator.cs pise datume u "round-trip" ("O") formatu, npr.
    // "2024-04-01T15:04:00.0000000Z" - ova metoda ih cita natrag u DateTime
    // s Kind=Utc. Koristimo AdjustToUniversal + AssumeUniversal eksplicitno
    // (ne oslanjamo se na default ponasanje DateTime.Parse) da datum ostane
    // identican bez obzira na lokalnu vremensku zonu stroja koji pokrece kod.
    public static DateTime ParseDate(string value)
    {
        return DateTime.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
    }
}
