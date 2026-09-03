using BenchmarkApp.Models;
using MongoDB.Driver;

namespace BenchmarkApp.Queries;

// Q1-Q5 na Mongo strani. Svaka metoda prima IMongoDatabase i sama dohvaca
// "users"/"posts" kolekcije - MongoDB.Driver kolekcije su, kao i Neo4j
// sesije, jeftini omotaci oko vec postojeceg connection poola, ne otvaraju
// novu konekciju svaki put.
public static class MongoQueries
{
    // Ova varijanta NEMA jedan tekst upita - svaka metoda ispod izvrsava
    // VISE tipiziranih Builders<T> odlazaka u bazu (npr. Q3 radi Find pa
    // In-filter pa opet Find), ne jedan aggregation pipeline. Prikazivanje
    // izmisljenog jedinstvenog MQL stringa za ovu varijantu bilo bi laz u
    // alatu ciji je smisao prikazati ISTINU o tome sto se izvrsava (v.
    // odluka #4 u uputama) - zato QueryCatalog za "mongo" varijantu prikazuje
    // ove C# isjecke, s jasnom napomenom da MongoDB ovdje nema jedan upit.
    public const string Q1Snippet =
        """
        users.Find(u => u.Id == id).FirstOrDefaultAsync()
        """;

    public const string Q2Snippet =
        """
        // followers popis je vec na dokumentu subjekta (denormalizirano)
        var subject = await users.Find(u => u.Id == id).FirstOrDefaultAsync();
        var top500Ids = subject.Followers.OrderBy(id => id, StringComparer.Ordinal).Take(500);
        await users.Find(Builders<UserDocument>.Filter.In(u => u.Id, top500Ids)).ToListAsync();
        """;

    public const string Q3Snippet =
        """
        var subject = await users.Find(u => u.Id == id).FirstOrDefaultAsync();
        var hop1Ids = subject.Following;
        var hop1Users = await users.Find(Builders<UserDocument>.Filter.In(u => u.Id, hop1Ids))
            .Project<UserDocument>(Builders<UserDocument>.Projection.Include(u => u.Following))
            .ToListAsync();
        // union svih hop1.Following u C#-u (HashSet), pa jos jedan Find po top 500 id-eva
        """;

    public const string Q4Snippet =
        """
        var subject = await users.Find(u => u.Id == id).FirstOrDefaultAsync();
        var feedPosts = await posts.Find(Builders<PostDocument>.Filter.In(p => p.AuthorId, subject.Following))
            .SortByDescending(p => p.CreatedAt).ThenBy(p => p.Id).Limit(50).ToListAsync();
        // pa DRUGI Find za autore, spoj u C#-u preko Dictionary-ja
        """;

    public const string Q5Snippet =
        """
        // BFS sloj-po-sloj: svaki hop je STVARAN Find po trenutnom frontieru
        var frontierDocs = await users.Find(Builders<UserDocument>.Filter.In(u => u.Id, frontier))
            .Project<UserDocument>(Builders<UserDocument>.Projection.Include(u => u.Id).Include(u => u.Following))
            .ToListAsync();
        // ponavlja se do maxHops puta ili dok se ne nade toId
        """;

    // Q1 - profil jednog korisnika po ID-u (Mongov "_id"). Jednostavan
    // lookup po primarnom kljucu, automatski indeksiran - nema poseban
    // indeks za ovo (v. Load/MongoLoader.cs).
    public static async Task<UserProfile?> Q1(IMongoDatabase database, string id)
    {
        var users = database.GetCollection<UserDocument>("users");
        UserDocument? doc = await users.Find(u => u.Id == id).FirstOrDefaultAsync();
        return doc is null ? null : new UserProfile(doc.Id, doc.Username, doc.Name, doc.CreatedAt);
    }

    // Q2 - popis pratitelja subjekta. "followers" popis je vec na dokumentu
    // subjekta (denormalizirano), pa NE treba pretrazivati druge korisnike
    // da bismo znali TKO prati subjekta - samo trebamo njihove profile.
    //
    // KRITICNO za determinizam (v. plan): "prvih 500" mora biti IDENTICNO
    // onome sto Neo4j odabere (ORDER BY f.id ASC LIMIT 500), inace Verifier
    // lazno pada za popularne korisnike (>500 pratitelja). Zato followers
    // popis SORTIRAMO OVDJE, U C#-U, PRIJE nego odrezemo na 500 - ne smijemo
    // prvo dovuci 500 proizvoljnih profila pa ih tek onda sortirati.
    //
    // StringComparer.Ordinal (NE goli OrderBy(id), koji bi po defaultu
    // sortirao po trenutnoj kulturi/locale-u stroja koji pokrece kod) je
    // obavezan jer Neo4jev "ORDER BY" sortira stringove po Unicode
    // codepointu - StringComparer.Ordinal radi tocno to u C#-u. Bez ovoga bi
    // se poredak mogao suptilno razlikovati (npr. kod velikih/malih slova)
    // i Verifier bi povremeno padao bez ocitog razloga.
    public static async Task<List<UserSummary>> Q2(IMongoDatabase database, string id)
    {
        var users = database.GetCollection<UserDocument>("users");

        UserDocument? subject = await users.Find(u => u.Id == id).FirstOrDefaultAsync();
        if (subject is null)
        {
            return new List<UserSummary>();
        }

        List<string> top500Ids = subject.Followers
            .OrderBy(followerId => followerId, StringComparer.Ordinal)
            .Take(500)
            .ToList();

        return await FetchSummaries(users, top500Ids);
    }

    // Q3 - prijatelji prijatelja (2 hopa), app-side fan-out (NAMJERNO bez
    // $graphLookup - v. OGRANICENJA.md): hop1 = koga subjekt prati (vec na
    // dokumentu), hop2 = JEDAN upit koji dovuce "following" popise SVIH
    // hop1-korisnika odjednom (ne upit po korisniku), pa union u C#-u.
    // Isti razlog za sortiranje+rezanje PRIJE zavrsnog Find-a kao u Q2.
    public static async Task<List<UserSummary>> Q3(IMongoDatabase database, string id)
    {
        var users = database.GetCollection<UserDocument>("users");

        UserDocument? subject = await users.Find(u => u.Id == id).FirstOrDefaultAsync();
        if (subject is null)
        {
            return new List<UserSummary>();
        }

        List<string> hop1Ids = subject.Following;
        if (hop1Ids.Count == 0)
        {
            return new List<UserSummary>();
        }

        // Jedan round-trip za SVE hop1 korisnike odjednom - projekcija samo
        // na "following" polje, ne trebamo cijeli dokument ovdje.
        var hop2Filter = Builders<UserDocument>.Filter.In(u => u.Id, hop1Ids);
        var hop2Projection = Builders<UserDocument>.Projection.Include(u => u.Following);
        List<UserDocument> hop1Users = await users.Find(hop2Filter)
            .Project<UserDocument>(hop2Projection)
            .ToListAsync();

        var friendsOfFriends = new HashSet<string>();
        foreach (UserDocument hop1User in hop1Users)
        {
            foreach (string candidateId in hop1User.Following)
            {
                friendsOfFriends.Add(candidateId);
            }
        }
        friendsOfFriends.Remove(id); // fof <> subjekt, isto kao Neo4jev "WHERE fof <> u"

        List<string> top500Ids = friendsOfFriends
            .OrderBy(fofId => fofId, StringComparer.Ordinal)
            .Take(500)
            .ToList();

        return await FetchSummaries(users, top500Ids);
    }

    // Q4 - feed: objave ljudi koje subjekt prati, najnovije prvo, limit 50.
    // "ThenBy(Id)" je isti tie-break razlog kao na Neo4j strani (deterministican
    // poredak kad je created_at identican na dvije objave).
    //
    // Autorova korisnicka imena dohvacamo DRUGIM, zasebnim upitom (ne
    // Mongov "$lookup" aggregation stage) - namjerno jednostavniji dvostruki
    // Find + in-memory spoj u C#-u preko Dictionary-ja, citljiviji za
    // studenta nego aggregation pipeline, i cinjenicno tocan opis onoga sto
    // Mongo strana ovdje radi (dva round-tripa, ne jedan).
    public static async Task<List<FeedItem>> Q4(IMongoDatabase database, string id)
    {
        var users = database.GetCollection<UserDocument>("users");
        var posts = database.GetCollection<PostDocument>("posts");

        UserDocument? subject = await users.Find(u => u.Id == id).FirstOrDefaultAsync();
        if (subject is null || subject.Following.Count == 0)
        {
            return new List<FeedItem>();
        }

        var postFilter = Builders<PostDocument>.Filter.In(p => p.AuthorId, subject.Following);
        List<PostDocument> feedPosts = await posts.Find(postFilter)
            .SortByDescending(p => p.CreatedAt)
            .ThenBy(p => p.Id)
            .Limit(50)
            .ToListAsync();

        if (feedPosts.Count == 0)
        {
            return new List<FeedItem>();
        }

        List<string> authorIds = feedPosts.Select(p => p.AuthorId).Distinct().ToList();
        var authorFilter = Builders<UserDocument>.Filter.In(u => u.Id, authorIds);
        List<UserDocument> authors = await users.Find(authorFilter).ToListAsync();
        Dictionary<string, string> usernameById = authors.ToDictionary(a => a.Id, a => a.Username);

        return feedPosts
            .Select(p => new FeedItem(p.Id, p.AuthorId, usernameById[p.AuthorId], p.Text, p.CreatedAt))
            .ToList();
    }

    // Q5 - najkraci USMJERENI put od fromId do toId preko "following" polja,
    // ogranicen na maxHops koraka. BFS (breadth-first search - pretraga po
    // slojevima: sve na udaljenosti 1, pa 2, pa 3...) je jedini nacin da
    // MongoDB nade najkraci put, jer nema ugradenu operaciju traversal-a
    // poput Neo4jevog shortestPath(). VAZNO (pravilo iz CLAUDE.md): svaki
    // hop je STVARAN upit prema bazi (Find po trenutnom frontieru) - C#
    // samo pamti VEC dobivene id-eve i roditelje izmedu poziva, ne simulira
    // traversal citajuci cijeli graf unaprijed u memoriju.
    public static async Task<PathResult> Q5(IMongoDatabase database, string fromId, string toId, int maxHops)
    {
        var users = database.GetCollection<UserDocument>("users");

        if (fromId == toId)
        {
            return new PathResult(new List<string> { fromId }, 0);
        }

        // parent[x] = cvor iz kojeg smo prvi put dosli do x - sluzi za
        // rekonstrukciju puta unatrag kad (ako) nademo toId.
        var parent = new Dictionary<string, string>();
        var visited = new HashSet<string> { fromId };
        var frontier = new List<string> { fromId };

        for (int hop = 1; hop <= maxHops; hop++)
        {
            var filter = Builders<UserDocument>.Filter.In(u => u.Id, frontier);
            var projection = Builders<UserDocument>.Projection.Include(u => u.Id).Include(u => u.Following);
            List<UserDocument> frontierDocs = await users.Find(filter)
                .Project<UserDocument>(projection)
                .ToListAsync();

            var nextFrontier = new List<string>();
            foreach (UserDocument doc in frontierDocs)
            {
                foreach (string neighborId in doc.Following)
                {
                    if (visited.Contains(neighborId))
                    {
                        continue; // vec posjecen u ranijem (kracem ili jednako kratkom) sloju
                    }

                    visited.Add(neighborId);
                    parent[neighborId] = doc.Id;
                    nextFrontier.Add(neighborId);

                    if (neighborId == toId)
                    {
                        // Nasli smo cilj - rekonstruiraj put unatrag preko parent[] i vrati odmah,
                        // ne treba dovrsiti ostatak ovog sloja.
                        return new PathResult(ReconstructPath(parent, fromId, toId), hop);
                    }
                }
            }

            if (nextFrontier.Count == 0)
            {
                break; // graf se "osusio" prije nego smo dosegli maxHops - nema puta
            }

            frontier = nextFrontier;
        }

        // Nismo nasli toId unutar maxHops koraka - ista konvencija kao Neo4j strana.
        return new PathResult(new List<string>(), -1);
    }

    private static List<string> ReconstructPath(Dictionary<string, string> parent, string fromId, string toId)
    {
        var reversed = new List<string> { toId };
        string current = toId;
        while (current != fromId)
        {
            current = parent[current];
            reversed.Add(current);
        }
        reversed.Reverse();
        return reversed;
    }

    private static async Task<List<UserSummary>> FetchSummaries(
        IMongoCollection<UserDocument> users,
        List<string> ids)
    {
        if (ids.Count == 0)
        {
            return new List<UserSummary>();
        }

        var filter = Builders<UserDocument>.Filter.In(u => u.Id, ids);
        List<UserDocument> docs = await users.Find(filter).ToListAsync();
        return docs.Select(d => new UserSummary(d.Id, d.Username, d.Name)).ToList();
    }
}
