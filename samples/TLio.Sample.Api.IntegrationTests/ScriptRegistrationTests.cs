using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;

namespace TLio.Sample.Api.IntegrationTests;

[TestFixture]
public class ScriptRegistrationTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new WebApplicationFactory<Program>();
        _client  = _factory.CreateClient();
    }

    [TearDown]
    public void TearDown()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Test]
    public async Task PostScripts_ValidJson_Returns201()
    {
        var body = """{"slug":"reg-test","script":"[]"}""";
        var response = await _client.PostAsync("/scripts",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That(response.Headers.Location?.ToString(), Does.Contain("reg-test"));
    }

    [Test]
    public async Task PostScripts_DuplicateSlug_Returns200WithReplaced()
    {
        var body = """{"slug":"dup-test","script":"[]"}""";
        await _client.PostAsync("/scripts", new StringContent(body, Encoding.UTF8, "application/json"));

        var response = await _client.PostAsync("/scripts",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var content = await response.Content.ReadAsStringAsync();
        Assert.That(content, Does.Contain("replaced"));
    }

    [Test]
    public async Task PostScripts_InvalidSlug_Returns400()
    {
        var body = """{"slug":"INVALID SLUG","script":"[]"}""";
        var response = await _client.PostAsync("/scripts",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task PostScripts_InvalidScriptBody_CompilesToEmptyScript_Returns201()
    {
        // The engine silently produces an empty script for non-JSON or non-array input — no compile error by design.
        var body = """{"slug":"empty-script","script":"not-a-valid-script!@#"}""";
        var response = await _client.PostAsync("/scripts",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
    }

    [Test]
    public async Task PostScripts_UnparsableBody_Returns400()
    {
        var response = await _client.PostAsync("/scripts",
            new StringContent("not json", Encoding.UTF8, "application/json"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task PostScripts_XmlBody_Returns201()
    {
        var body = "<registration><slug>xml-slug</slug><script>[]</script></registration>";
        var response = await _client.PostAsync("/scripts",
            new StringContent(body, Encoding.UTF8, "application/xml"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
    }
}
