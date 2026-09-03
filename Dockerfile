# Dvostupanjski build (Faza 6 plana): SDK image samo za kompajliranje, manji
# ASP.NET runtime image za izvodenje - runtime image nema kompajler ni NuGet
# cache, samo ono sto je stvarno potrebno za pokretanje aplikacije.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY BenchmarkApp.csproj ./
RUN dotnet restore BenchmarkApp.csproj

COPY . .
RUN dotnet publish BenchmarkApp.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# WORKDIR /app + Program.cs "Directory.GetCurrentDirectory() + data/raw"
# MORA pogoditi tocno /app/data/raw - to je mount tocka iz docker-compose.yml
# ("./Data/raw:/app/data/raw:ro"). Bind-mount putanja je
# case-sensitive UNUTAR Linux spremnika (na disku je "Data" veliko D, ovdje
# namjerno "data" malo d - v. Program.cs komentar i uputa uz ovu izmjenu).
EXPOSE 8080
ENTRYPOINT ["dotnet", "BenchmarkApp.dll", "serve"]
