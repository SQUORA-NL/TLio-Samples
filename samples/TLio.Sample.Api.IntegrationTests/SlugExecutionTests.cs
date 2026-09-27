using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;

namespace TLio.Sample.Api.IntegrationTests;

[TestFixture]
public class SlugExecutionTests
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
    public async Task Run_UnknownSlug_Returns404()
    {
        var response = await _client.PostAsync("/run/no-such-slug",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Run_InvalidSlugFormat_Returns400()
    {
        var response = await _client.PostAsync("/run/INVALID SLUG",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Run_RegisteredSlug_WithJson_ReturnsTransformedJson()
    {
        // Register a simple pass-through script
        var scriptBody = """{"slug":"exec-test","script":"[]"}""";
        var regResponse = await _client.PostAsync("/scripts",
            new StringContent(scriptBody, Encoding.UTF8, "application/json"));
        Assert.That(regResponse.StatusCode, Is.EqualTo(HttpStatusCode.Created));

        // Execute it with JSON input
        var input = """{"name":"world"}""";
        var runResponse = await _client.PostAsync("/run/exec-test",
            new StringContent(input, Encoding.UTF8, "application/json"));

        Assert.That(runResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(runResponse.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/json"));
    }

    [Test]
    public async Task Run_RegisteredSlug_WithXml_ReturnsXml()
    {
        var scriptBody = """{"slug":"exec-xml","script":"[]"}""";
        await _client.PostAsync("/scripts",
            new StringContent(scriptBody, Encoding.UTF8, "application/json"));

        var xml = "<root><value>42</value></root>";
        var runResponse = await _client.PostAsync("/run/exec-xml",
            new StringContent(xml, Encoding.UTF8, "application/xml"));

        Assert.That(runResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(runResponse.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/xml"));
    }

    [Test]
    public async Task Run_PlainTextBody_FallsBackToYamlAndReturnsOk()
    {
        // YAML accepts any scalar string, so plain text always resolves to YAML format.
        var scriptBody = """{"slug":"yaml-fallback","script":"[]"}""";
        await _client.PostAsync("/scripts",
            new StringContent(scriptBody, Encoding.UTF8, "application/json"));

        var request = new HttpRequestMessage(HttpMethod.Post, "/run/yaml-fallback");
        request.Content = new StringContent("any plain text", Encoding.UTF8, "text/plain");

        var response = await _client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/yaml"));
    }
}
