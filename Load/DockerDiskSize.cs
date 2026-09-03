using System.Diagnostics;

namespace BenchmarkApp.Load;

// Mjeri koliko prostora na disku zauzima direktorij S PODACIMA unutar
// Docker kontejnera (npr. Neo4jeva /data ili Mongova /data/db). Koristimo
// "docker exec <container> du -sb <put>" umjesto citanja Docker volumena
// izravno s hosta, jer je to prijenosno (ne treba znati gdje Docker Desktop
// stvarno drzi volumene na disku) i radi identicno na svakoj instalaciji.
public static class DockerDiskSize
{
    // Vraca velicinu u megabajtima, ili 0 ako mjerenje ne uspije (npr.
    // kontejner nije pokrenut) - punjenje baze se svejedno ne smije srusiti
    // samo zato sto mjerenje diska nije uspjelo, zato ovo namjerno NE baca
    // iznimku nego ispise upozorenje i vrati 0.
    public static double GetDirectorySizeMb(string containerName, string pathInContainer)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "docker",
                ArgumentList = { "exec", containerName, "du", "-sb", pathInContainer },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                Console.WriteLine($"UPOZORENJE: nisam mogao pokrenuti 'docker exec' za {containerName}.");
                return 0;
            }

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                Console.WriteLine($"UPOZORENJE: 'du -sb {pathInContainer}' u {containerName} nije uspio (exit {process.ExitCode}).");
                return 0;
            }

            // "du -sb" ispisuje "<bajtovi>\t<put>" - uzimamo samo prvi dio
            string bytesText = output.Split('\t', 2)[0].Trim();
            if (!long.TryParse(bytesText, out long bytes))
            {
                Console.WriteLine($"UPOZORENJE: ne mogu parsirati izlaz 'du' za {containerName}: '{output}'.");
                return 0;
            }

            return bytes / 1024.0 / 1024.0;
        }
        catch (Exception ex)
        {
            // Npr. docker CLI uopce nije dostupan u PATH-u - ne prekidamo punjenje zbog toga.
            Console.WriteLine($"UPOZORENJE: mjerenje velicine diska za {containerName} nije uspjelo: {ex.Message}");
            return 0;
        }
    }
}
