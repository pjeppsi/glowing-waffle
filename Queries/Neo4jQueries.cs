using BenchmarkApp.Models;
using Neo4j.Driver;

namespace BenchmarkApp.Queries;

// Q1-Q5 na Neo4j strani. Svaka metoda otvara SVOJU sesiju (session-per-call)
// - Neo4j.Driver sesije su jeftin "omotac" oko konekcije iz vec postojeceg
// connection poola unutar IDriver-a, ne otvaraju novu TCP konekciju svaki
// put - to je namjeran, uobicajen nacin koristenja ovog drivera za citanje.
public static class Neo4jQueries
{
    // Tekst upita kao JEDINI izvor istine (Faza 2 plana): metode ispod
    // izvrsavaju bas OVA polja, a UI (Queries/QueryCatalog.cs) ih prikazuje
    // iz ISTIH polja - nema odvojene "kopije za prikaz" koja bi se mogla
    // razici od onoga sto se stvarno salje bazi.
    public const string Q1Cypher =
        """
        MATCH (u:User {id: $id})
        RETURN u.id AS id, u.username AS username, u.name AS name, u.created_at AS created_at
        """;

    public const string Q2Cypher =
        """
        MATCH (u:User {id: $id})<-[:FOLLOWS]-(f)
        RETURN f.id AS id, f.username AS username, f.name AS name
        ORDER BY f.id ASC
        LIMIT 500
        """;

    public const string Q3Cypher =
        """
        MATCH (u:User {id: $id})-[:FOLLOWS]->()-[:FOLLOWS]->(fof)
        WHERE fof <> u
        RETURN DISTINCT fof.id AS id, fof.username AS username, fof.name AS name
        ORDER BY fof.id ASC
        LIMIT 500
        """;

    public const string Q4Cypher =
        """
        MATCH (me:User {id: $id})-[:FOLLOWS]->(u)-[:POSTED]->(p)
        RETURN p.id AS post_id, u.id AS author_id, u.username AS author_username,
               p.text AS text, p.created_at AS created_at
        ORDER BY p.created_at DESC, p.id ASC
        LIMIT 50
        """;

    // Q1 - profil jednog korisnika po ID-u. Ocekivano: 0 ili 1 red.
    public static async Task<UserProfile?> Q1(IDriver driver, string id)
    {
        await using var session = driver.AsyncSession();

        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(Q1Cypher, new { id });

            var records = await cursor.ToListAsync();
            return records.Count == 0 ? null : MapUserProfile(records[0]);
        });
    }

    // Q2 - popis pratitelja subjekta (1 hop, USMJERENO suprotno od FOLLOWS:
    // "<-[:FOLLOWS]-" znaci "netko prati MENE"). LIMIT 500 + ORDER BY id ASC
    // (v. napomenu u planu o determinizmu - Mongo strana mora birati ISTIH
    // 500 po istom kriteriju, inace Verifier lazno pada za popularne korisnike).
    public static async Task<List<UserSummary>> Q2(IDriver driver, string id)
    {
        await using var session = driver.AsyncSession();

        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(Q2Cypher, new { id });

            var records = await cursor.ToListAsync();
            return records.Select(MapUserSummary).ToList();
        });
    }

    // Q3 - prijatelji prijatelja (2 hopa), bez samog subjekta, bez duplikata
    // (DISTINCT - moguce je doci do istog fof-a preko vise razlicitih 1-hop
    // posrednika). Isti LIMIT 500 + ORDER BY razlog kao Q2.
    public static async Task<List<UserSummary>> Q3(IDriver driver, string id)
    {
        await using var session = driver.AsyncSession();

        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(Q3Cypher, new { id });

            var records = await cursor.ToListAsync();
            return records.Select(MapUserSummary).ToList();
        });
    }

    // Q4 - feed: objave ljudi koje subjekt prati, najnovije prvo. Tie-break
    // "p.id ASC" osigurava deterministican poredak kad dvije objave imaju
    // TOCNO isti created_at (moguce kod nasumicno generiranih datuma) -
    // bez njega bi LIMIT 50 mogao svaki put uzeti drukciji rub-slucaj.
    public static async Task<List<FeedItem>> Q4(IDriver driver, string id)
    {
        await using var session = driver.AsyncSession();

        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(Q4Cypher, new { id });

            var records = await cursor.ToListAsync();
            return records.Select(MapFeedItem).ToList();
        });
    }

    // Q5 - najkraci USMJERENI put od $from do $to, ogranicen na maxHops
    // koraka. VAZNO: granica varijabilne duljine ("*..N") u Cypher patternu
    // MORA biti literal u tekstu upita - Cypher ne dopusta parametar na tom
    // mjestu ("*..$maxHops" nije valjan Cypher). Zato se maxHops ubacuje
    // string-interpolacijom, JEDINO mjesto u cijelom projektu gdje se to
    // radi umjesto preko $parametara - sigurno je jer maxHops dolazi iz
    // appsettings.json (developer-kontrolirana konfiguracija), ne iz
    // korisnickog unosa, pa nema opasnosti od Cypher-injectiona.
    //
    // Koristimo C# 11 "raw string interpolation" ($$""" ... """) umjesto
    // obicnog $"...": time se DUPLI "{{maxHops}}" tumaci kao mjesto za
    // interpolaciju, a JEDNOSTRUKE Cypherove vitičaste zagrade poput
    // "{id: $from}" ostaju cist, necitljiv Cypher tekst bez potrebe za
    // rucnim escapeanjem "{{" / "}}" kao kod obicnih interpoliranih stringova.
    // maxHops ide TEKSTUALNOM interpolacijom (ne kao $parametar) jer Cypher
    // ne dopusta parametar unutar granice varijabilne duljine ("*..N") - v.
    // opsiran komentar gore na metodi Q5. Izdvojeno u javnu metodu da je i
    // ovo, kao i ostali upiti, JEDINI izvor teksta - QueryCatalog za prikaz
    // poziva TOCNO ovu metodu, ne drzi vlastitu kopiju.
    public static string Q5Cypher(int maxHops) => $$"""
        MATCH p = shortestPath((a:User {id: $from})-[:FOLLOWS*..{{maxHops}}]->(b:User {id: $to}))
        RETURN [n IN nodes(p) | n.id] AS path, length(p) AS len
        """;

    public static async Task<PathResult> Q5(IDriver driver, string fromId, string toId, int maxHops)
    {
        await using var session = driver.AsyncSession();

        string query = Q5Cypher(maxHops);

        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(query, new { from = fromId, to = toId });
            var records = await cursor.ToListAsync();

            // Prazan rezultat = MATCH nije nasao nijedan put unutar maxHops
            // granice (ne baca gresku, jednostavno nema redaka) - konvencija
            // iz Models/PathResult.cs: Length=-1, prazan Path.
            if (records.Count == 0)
            {
                return new PathResult(new List<string>(), -1);
            }

            var path = records[0]["path"].As<List<string>>();
            int length = records[0]["len"].As<int>();
            return new PathResult(path, length);
        });
    }

    private static UserProfile MapUserProfile(IRecord record)
    {
        return new UserProfile(
            record["id"].As<string>(),
            record["username"].As<string>(),
            record["name"].As<string>(),
            ToUtcDateTime(record["created_at"]));
    }

    private static UserSummary MapUserSummary(IRecord record)
    {
        return new UserSummary(
            record["id"].As<string>(),
            record["username"].As<string>(),
            record["name"].As<string>());
    }

    private static FeedItem MapFeedItem(IRecord record)
    {
        return new FeedItem(
            record["post_id"].As<string>(),
            record["author_id"].As<string>(),
            record["author_username"].As<string>(),
            record["text"].As<string>(),
            ToUtcDateTime(record["created_at"]));
    }

    // Cypher datetime() se u Neo4j.Driver mapira u tip ZonedDateTime, NE
    // izravno u System.DateTime (driver namjerno ne dopusta ".As<DateTime>()"
    // direktno na njemu - baca "Conversion of ZonedDateTime to DateTime is
    // not supported", provjereno rucno prije pisanja ovog koda) jer
    // ZonedDateTime uz trenutak u vremenu nosi i vremensku zonu, sto obican
    // DateTime ne moze potpuno predstaviti. Podaci su spremljeni kao UTC
    // (v. Neo4jLoader.cs), pa je .UtcDateTime tocno ono sto zelimo.
    private static DateTime ToUtcDateTime(object value)
    {
        return value.As<ZonedDateTime>().UtcDateTime;
    }
}
