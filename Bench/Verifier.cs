namespace BenchmarkApp.Bench;

// Verifier NE zna nista o Neo4j-u, Mongu, ni o konkretnim upitima Q1-Q5 -
// namjerno je "glup" i opcenit. Prima dva popisa ID-eva (ili dvije duljine
// puta) i kaze jesu li "isti" u smislu koji nam treba za ovaj projekt.
//
// Zasto usporedba ide UVIJEK preko ID-eva, a ne preko cijelih recorda
// (npr. ugradene C# record jednakosti)? Jer dva rezultata mogu sadrzavati
// ISTI skup korisnika u DRUKCIJEM poretku liste (npr. Neo4j i Mongo
// razlicito interno iteriraju kroz $in upit) - List<T>.SequenceEqual ili
// record-jednakost nad listom bi to lazno prijavila kao "razlicito", iako
// je semanticki isti rezultat. HashSet<string>.SetEquals ignorira poredak
// i duplikate - upravo ono sto "semanticki isti rezultat" znaci u ovom
// projektu (v. definicija u planu).
//
// Koji string se smatra "ID-em" ovisi o upitu (Q1-Q3 imaju Id, Q4 ima
// PostId, ne Id) - Verifier to NE pretpostavlja sam; pozivatelj (buduci
// "verify" kod u Fazi 4, kad upiti postoje) mora eksplicitno reci koje
// polje uzima, npr. .Select(x => x.PostId) za Q4. Ovo je namjerno - da
// generic helper po imenu polja ne bi tiho posegnuo za krivim podatkom.
public static class Verifier
{
    // Usporeduje dva skupa ID-eva (npr. rezultat Q1/Q2/Q3/Q4 s Neo4j i
    // Mongo strane, vec svedenih na string ID-eve od strane pozivatelja).
    // Vraca true ako su identicni SKUPOVI (poredak i duplikati ne igraju
    // ulogu), inace ispise ŠTO se tocno razlikuje i vrati false.
    public static bool CompareIdSets(
        string queryLabel,
        string subjectLabel,
        IEnumerable<string> neo4jIds,
        IEnumerable<string> mongoIds)
    {
        var neo4jSet = neo4jIds.ToHashSet();
        var mongoSet = mongoIds.ToHashSet();

        if (neo4jSet.SetEquals(mongoSet))
        {
            return true;
        }

        var onlyInNeo4j = neo4jSet.Except(mongoSet).OrderBy(id => id, StringComparer.Ordinal).ToList();
        var onlyInMongo = mongoSet.Except(neo4jSet).OrderBy(id => id, StringComparer.Ordinal).ToList();

        Console.WriteLine($"NEPODUDARANJE [{queryLabel}] subjekt={subjectLabel}:");
        Console.WriteLine($"  Neo4j vratio {neo4jSet.Count} ID-eva, Mongo vratio {mongoSet.Count} ID-eva.");
        if (onlyInNeo4j.Count > 0)
        {
            Console.WriteLine($"  Samo u Neo4j ({onlyInNeo4j.Count}): {FormatSample(onlyInNeo4j)}");
        }
        if (onlyInMongo.Count > 0)
        {
            Console.WriteLine($"  Samo u Mongo ({onlyInMongo.Count}): {FormatSample(onlyInMongo)}");
        }

        return false;
    }

    // Trosmjerna verzija (Faza 3 plana): usporeduje SVA TRI imenovana skupa
    // (neo4j, mongo, mongo-raw) MEDUSOBNO, ne samo par po par - jedan poziv,
    // jedna provjera po upitu po subjektu (broj provjera OSTAJE 50, ne
    // raste na 150 - v. odluka #2 u uputama). Ako se bilo koja dva skupa
    // razlikuju, ispisuje TOCNO koja varijanta odudara od kojih.
    public static bool CompareIdSets(
        string queryLabel,
        string subjectLabel,
        params (string Name, IEnumerable<string> Ids)[] namedSets)
    {
        var sets = namedSets.Select(n => (n.Name, Set: n.Ids.ToHashSet())).ToList();

        bool allEqual = sets.All(s => s.Set.SetEquals(sets[0].Set));
        if (allEqual)
        {
            return true;
        }

        Console.WriteLine($"NEPODUDARANJE [{queryLabel}] subjekt={subjectLabel}:");
        foreach ((string name, HashSet<string> set) in sets)
        {
            Console.WriteLine($"  {name}: {set.Count} ID-eva.");
        }

        for (int i = 0; i < sets.Count; i++)
        {
            for (int j = i + 1; j < sets.Count; j++)
            {
                var onlyInFirst = sets[i].Set.Except(sets[j].Set).OrderBy(id => id, StringComparer.Ordinal).ToList();
                var onlyInSecond = sets[j].Set.Except(sets[i].Set).OrderBy(id => id, StringComparer.Ordinal).ToList();

                if (onlyInFirst.Count > 0)
                {
                    Console.WriteLine($"  Samo u {sets[i].Name}, ne u {sets[j].Name} ({onlyInFirst.Count}): {FormatSample(onlyInFirst)}");
                }
                if (onlyInSecond.Count > 0)
                {
                    Console.WriteLine($"  Samo u {sets[j].Name}, ne u {sets[i].Name} ({onlyInSecond.Count}): {FormatSample(onlyInSecond)}");
                }
            }
        }

        return false;
    }

    // Q5 (najkraci put) se namjerno NE uspoređuje preko niza ID-eva u putu -
    // kad postoji vise najkracih putova iste duljine, Neo4j i Mongo (BFS)
    // mogu legitimno vratiti RAZLICITE, jednako ispravne putove (npr. oba
    // duljine 3, ali kroz drukcije posrednike). Duljina puta je jedino sto
    // MORA biti isto ako su oba upita ispravna - zato usporedujemo samo nju.
    public static bool CompareLength(string queryLabel, string subjectLabel, int neo4jLength, int mongoLength)
    {
        if (neo4jLength == mongoLength)
        {
            return true;
        }

        Console.WriteLine(
            $"NEPODUDARANJE [{queryLabel}] subjekt={subjectLabel}: " +
            $"Neo4j duljina={neo4jLength}, Mongo duljina={mongoLength}.");

        return false;
    }

    // Trosmjerna verzija - isto nacelo kao gornji CompareLength, ali za sve
    // tri varijante odjednom (koristi VerifyRunner.VerifyQ5).
    public static bool CompareLength(string queryLabel, string subjectLabel, params (string Name, int Length)[] namedLengths)
    {
        bool allEqual = namedLengths.All(n => n.Length == namedLengths[0].Length);
        if (allEqual)
        {
            return true;
        }

        string details = string.Join(", ", namedLengths.Select(n => $"{n.Name} duljina={n.Length}"));
        Console.WriteLine($"NEPODUDARANJE [{queryLabel}] subjekt={subjectLabel}: {details}.");

        return false;
    }

    // Ispisuje najvise 10 ID-eva u razlici (ne cijeli skup ako je ogroman) -
    // dovoljno za ljudsko dijagnosticiranje problema, bez zatrpavanja konzole.
    private static string FormatSample(List<string> ids)
    {
        const int maxShown = 10;
        string joined = string.Join(", ", ids.Take(maxShown));
        return ids.Count > maxShown ? $"{joined}, ... (+{ids.Count - maxShown} jos)" : joined;
    }
}

// Broji koliko je pojedinacnih provjera (po upitu, po subjektu) proslo i
// palo tijekom cijele "dotnet run -- verify" vožnje, i na kraju odlucuje
// exit kod procesa - Program.cs ce (u Fazi 4, kad "verify" bude ozicen na
// stvarne upite) koristiti AllPassed da odluci smije li se "bench" uopce
// pokrenuti (plan zabranjuje mjerenje dok verify ne prode cist).
public sealed class VerificationSummary
{
    private int _totalChecks;
    private int _failedChecks;

    public void Record(bool passed)
    {
        _totalChecks++;
        if (!passed)
        {
            _failedChecks++;
        }
    }

    public bool AllPassed => _failedChecks == 0;

    public void PrintSummary()
    {
        int passedChecks = _totalChecks - _failedChecks;
        Console.WriteLine();
        Console.WriteLine($"Verifikacija: {passedChecks}/{_totalChecks} provjera proslo.");
        Console.WriteLine(AllPassed
            ? "Svi rezultati su semanticki identicni na obje baze."
            : $"{_failedChecks} provjera NIJE proslo - vidi ispis iznad za detalje.");
    }
}
