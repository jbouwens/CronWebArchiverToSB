using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Messaging.ServiceBus;
using Cronos;
using FlareSolverrSharp.Solvers;
using Microsoft.Extensions.Logging;

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole()
        .SetMinimumLevel(LogLevel.Information);
});
var logger = loggerFactory.CreateLogger("WebScraper");
var configuration = LoadConfiguration();
var scraper = new WebScraper(logger, configuration.FlareSolverrUrl);

try
{
    await RunScrapingLoop(configuration, scraper);
}
catch (Exception ex)
{
    logger.LogCritical(ex, "Fatal error in scraping application");
    throw;
}
finally
{
    await scraper.CleanupAsync();
}
return;

async Task RunScrapingLoop(Configuration config, WebScraper scraper)
{
    var now = DateTime.UtcNow;
    foreach (var item in config.UrlsToScrape)
    {
        item.Initialize(now);
    }

    while (true)
    {
        var nextOccurrenceTime = config.UrlsToScrape
            .Where(i => i.NextOccurrence.HasValue)
            .Select(i => i.NextOccurrence!.Value)
            .MinBy(x => x);

        if (nextOccurrenceTime == default)
        {
            logger.LogInformation("No more scheduled tasks. Exiting...");
            break;
        }

        var tasksToRun = config.UrlsToScrape
            .Where(i => i.NextOccurrence == nextOccurrenceTime)
            .ToList();

        now = DateTime.UtcNow;
        var delay = nextOccurrenceTime - now;
        if (delay > TimeSpan.Zero)
        {
            logger.LogInformation("Next scheduled tasks at {NextRunTime} UTC. Waiting {Delay} seconds...",
                nextOccurrenceTime, delay.TotalSeconds);
            await Task.Delay(delay);
        }

        var scrapingTasks = tasksToRun.Select(item => 
            scraper.ScrapeAndQueueAsync(item, config.ServiceBusConnectionString));
        await Task.WhenAll(scrapingTasks);

        now = DateTime.UtcNow;
        foreach (var item in tasksToRun)
        {
            item.UpdateNextOccurrence(now);
        }
    }
}

Configuration LoadConfiguration()
{
    var appSettingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
    if (File.Exists(appSettingsPath))
    {
        try
        {
            var config = JsonSerializer.Deserialize<Configuration>(
                File.ReadAllText(appSettingsPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (config != null)
            {
                logger.LogInformation("Loaded configuration from {ConfigPath}", appSettingsPath);
                return config;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load configuration from {ConfigPath}", appSettingsPath);
        }
    }

    var localSettingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "local.settings.json");
    return File.Exists(localSettingsPath)
        ? JsonSerializer.Deserialize<Configuration>(
              File.ReadAllText(localSettingsPath),
              new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
          ?? CreateDefaultConfiguration()
        : CreateDefaultConfiguration();
}

Configuration CreateDefaultConfiguration()
{
    return new Configuration
    {
        ServiceBusConnectionString = Environment.GetEnvironmentVariable("ServiceBusConnectionString") 
            ?? throw new InvalidOperationException("ServiceBusConnectionString not configured"),
        FlareSolverrUrl = Environment.GetEnvironmentVariable("FlareSolverrUrl")
            ?? "http://localhost:8191",
        UrlsToScrape = new List<UrlQueueItem>()
    };
}

public class WebScraper
{
    private readonly FlareSolverr _flareSolverr;
    private readonly ILogger _logger;
    private readonly HashSet<string> _managedSessions = new();
    private readonly Dictionary<string, string> _urlToSessionMap = new();

    public WebScraper(ILogger logger, string flareSolverrUrl)
    {
        _logger = logger;
        _flareSolverr = new FlareSolverr(flareSolverrUrl);
    }

    public async Task ScrapeAndQueueAsync(UrlQueueItem item, string serviceBusConnectionString)
    {
        try
        {
            _logger.LogInformation("Starting scrape for {Url}", item.Url);
            var sessionId = await GetOrCreateSessionForUrlAsync(item.Url);
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri(item.Url));
            var response = await _flareSolverr.Solve(request, sessionId);
            if (response.Status != "ok")
            {
                _logger.LogError("Failed to get content for {Url}. Status: {Status}", item.Url, response.Status);
                return;
            }
            string contentType = response.Solution.Headers.ContentType ?? "text/html";
            await SendToServiceBusQueue(item, response.Solution.Response, contentType, serviceBusConnectionString);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing '{Url}'", item.Url);
        }
    }

    private async Task<string> GetOrCreateSessionForUrlAsync(string url)
    {
        if (_urlToSessionMap.TryGetValue(url, out var existingSession))
        {
            try
            {
                var testRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(url));
                await _flareSolverr.Solve(testRequest, existingSession);
                return existingSession;
            }
            catch
            {
                _urlToSessionMap.Remove(url);
                _managedSessions.Remove(existingSession);
                _logger.LogWarning("Session {SessionId} appears invalid, creating new one", existingSession);
            }
        }

        var response = await _flareSolverr.CreateSession();
        if (response.Status != "ok")
        {
            throw new Exception($"Failed to create session: {response.Message}");
        }

        var sessionId = response.Session;
        _urlToSessionMap[url] = sessionId;
        _managedSessions.Add(sessionId);
        _logger.LogInformation("Created new FlareSolverr session: {SessionId}", sessionId);
        return sessionId;
    }

    private async Task SendToServiceBusQueue(UrlQueueItem item, string content, string contentType, string connectionString)
    {
        var client = new ServiceBusClient(connectionString);
        var sender = client.CreateSender(item.QueueName);
        byte[] messageBody;

        if (content.Contains("<pre"))
        {
            var startIndex = content.IndexOf("<pre");
            startIndex = content.IndexOf(">", startIndex) + 1;
            var endIndex = content.IndexOf("</pre>", startIndex);
            content = content.Substring(startIndex, endIndex - startIndex);
            contentType = "application/json";
        }
        
        if (contentType.Contains("json"))
        {
            try
            {
                using var doc = JsonDocument.Parse(content);
                messageBody = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions 
                { 
                    WriteIndented = true 
                }));
            }
            catch (JsonException)
            {
                _logger.LogWarning("Content claimed to be JSON but failed to parse. Treating as plain text.");
                messageBody = Encoding.UTF8.GetBytes(content);
            }
        }
        else
        {
            messageBody = Encoding.UTF8.GetBytes(content);
        }

        var message = new ServiceBusMessage(messageBody)
        {
            ContentType = contentType,
            Subject = item.Url,
            ApplicationProperties =
            {
                { "ScrapedAt", DateTime.UtcNow.ToString("O") },
                { "SourceUrl", item.Url }
            }
        };

        await sender.SendMessageAsync(message);
        _logger.LogInformation("Successfully sent {ContentType} content from '{Url}' to queue '{QueueName}'",
            contentType, item.Url, item.QueueName);

        await sender.DisposeAsync();
        await client.DisposeAsync();
    }

    public async Task CleanupAsync()
    {
        try
        {
            foreach (var session in _managedSessions)
            {
                await _flareSolverr.DestroySession(session);
                _logger.LogInformation("Destroyed FlareSolverr session: {SessionId}", session);
            }
            _managedSessions.Clear();
            _urlToSessionMap.Clear();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during session cleanup");
        }
    }
}

public class Configuration
{
    public string ServiceBusConnectionString { get; set; } = string.Empty;
    public string FlareSolverrUrl { get; set; } = string.Empty;
    public List<UrlQueueItem> UrlsToScrape { get; set; } = new();
}

public class UrlQueueItem
{
    public string Url { get; set; } = string.Empty;
    public string QueueName { get; set; } = string.Empty;
    public string CronExpression { get; set; } = string.Empty;

    [JsonIgnore] public CronExpression? Cron { get; private set; }
    [JsonIgnore] public DateTime? NextOccurrence { get; private set; }

    public void Initialize(DateTime now)
    {
        Cron = Cronos.CronExpression.Parse(CronExpression);
        NextOccurrence = Cron.GetNextOccurrence(now);
    }

    public void UpdateNextOccurrence(DateTime now)
    {
        NextOccurrence = Cron?.GetNextOccurrence(now);
    }
}