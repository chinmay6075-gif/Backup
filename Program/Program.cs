using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Agents.CopilotStudio.Client;
using CopilotStudioFunction;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices((context, services) =>
    {
        // Load Copilot Studio connection settings from environment / local.settings.json
        var settings = new AgentConnectionSettings(
            context.Configuration.GetSection("CopilotStudioClientSettings"));

        services.AddSingleton(settings);

        // Create a fresh delegating handler for each HttpClient pipeline.
        services.AddTransient<OBOTokenHandler>();

        // Named HttpClient used internally by CopilotClient
        services.AddHttpClient("mcs")
            .AddHttpMessageHandler<OBOTokenHandler>();

        // Transient CopilotClient — a fresh instance per function invocation
        services.AddTransient<CopilotClient>(sp =>
        {
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<CopilotClient>();
            return new CopilotClient(
                sp.GetRequiredService<AgentConnectionSettings>(),
                sp.GetRequiredService<IHttpClientFactory>(),
                logger,
                "mcs");
        });
    })
    .Build();

host.Run();
