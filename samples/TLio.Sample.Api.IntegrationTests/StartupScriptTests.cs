using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;

namespace TLio.Sample.Api.IntegrationTests;

[TestFixture]
public class StartupScriptTests
{
    private static string CreateTempConfig(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"tltest-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Test]
    public async Task StartupLoader_LoadsSlugFromConfig_SlugIsExecutable()
    {
        var configPath = CreateTempConfig("""[{"slug":"startup-slug","script":"[]"}]""");
        try
        {
            using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(host =>
                    host.ConfigureAppConfiguration((_, cfg) =>
                        cfg.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["TLIO_SCRIPTS_CONFIG"] = configPath
                        })));

            using var client = factory.CreateClient();

            var response = await client.PostAsync("/run/startup-slug",
                new StringContent("{}", Encoding.UTF8, "application/json"));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    [Test]
    public async Task StartupLoader_MissingConfig_AppStartsNormally()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(host =>
                host.ConfigureAppConfiguration((_, cfg) =>
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["TLIO_SCRIPTS_CONFIG"] = "/nonexistent/path/scripts.json"
                    })));

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/scripts");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task StartupLoader_InvalidEntryInConfig_SkipsItAndContinues()
    {
        var configPath = CreateTempConfig("""
            [
              {"slug":"","script":"[]"},
              {"slug":"valid-startup","script":"[]"}
            ]
            """);
        try
        {
            using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(host =>
                    host.ConfigureAppConfiguration((_, cfg) =>
                        cfg.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["TLIO_SCRIPTS_CONFIG"] = configPath
                        })));

            using var client = factory.CreateClient();

            var response = await client.PostAsync("/run/valid-startup",
                new StringContent("{}", Encoding.UTF8, "application/json"));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }
        finally
        {
            File.Delete(configPath);
        }
    }
}
