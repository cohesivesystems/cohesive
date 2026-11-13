using System.Net;
using Cohesive.Integrations;
using static Cohesive.Examples.Integrations.HistoricalMarketDataExamples;

namespace Cohesive.Examples.Integrations;

/// <summary>Shows where a host-owned HTTP client attaches to the portable historical-data declaration.</summary>
public sealed class ConnectionRuntimeExamples
{
    /// <summary>Resolves an exact binding and executes an offline HTTP fixture through the registered application port.</summary>
    /// <returns>Completes after the simulated response is read.</returns>
    [Fact]
    public async Task HostAttachesClientToExactProfileAndConnection()
    {
        var profile = ProviderOperationProfile.Define("example/prices", "v1", "fixture", "daily-prices", "HTTPS");
        var connection = IntegrationConnection.Define("example/research", "v1", "fixture", "research-account", "HTTPS",
            "https://market-data.example");
        var logical = ConnectionReference.Named("market-data");
        var model = new HistoricalMarketData();
        var selection = CreateSelection();
        var compiled = model.Collect.For(selection)
            .Use(model.Collect.Provider(profile).Provides(model.RetainedResponse))
            .Through(logical).ForConsumer(new("research", "fixture", "bars", selection.Instrument))
            .Compile(new ConnectionBindings().Bind(logical, connection));
        Assert.True(compiled.IsSuccessful);

        // Physical clients and secret resolution live in the host, outside the canonical documents.
        using var transport = new FixtureTransport();
        using var http = new HttpClient(transport)
        {
            BaseAddress = new(connection.Document.GetDefinition<IntegrationConnectionDefinition>().Endpoint)
        };
        var handler = new HistoricalPricesClient(http);
        var runtime = new IntegrationRuntime().Register(profile, connection, handler);
        var selectedHandler = runtime.Resolve<HistoricalPricesClient>(compiled);
        var response = await selectedHandler.ReadAsync(selection.Symbol, CancellationToken.None);
        Assert.Equal("[]", response);
        Assert.Equal("https://market-data.example/daily/SPY", transport.RequestUri!.AbsoluteUri);
        // This is an acquisition fixture only. Coverage, publication and progress use their own declared handlers.
        // An empty response here does not discharge the historical-coverage requirement.
    }

    sealed class HistoricalPricesClient(HttpClient http)
    {
        internal Task<string> ReadAsync(string symbol, CancellationToken cancellationToken) =>
            http.GetStringAsync("daily/" + Uri.EscapeDataString(symbol), cancellationToken);
    }

    sealed class FixtureTransport : HttpMessageHandler
    {
        internal Uri? RequestUri { get; private set; }
        /// <summary>Produces an offline response and captures the request endpoint.</summary>
        /// <param name="request">Outgoing request.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A successful synthetic empty response; no coverage claim.</returns>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        }
    }
}
