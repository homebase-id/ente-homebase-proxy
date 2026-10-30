using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EnteHomebaseProxy.Tests;

/// <summary>
/// The real proxy on a free localhost port (real Kestrel, real HTTP), with
/// every log line it writes captured for inspection.
/// </summary>
public sealed class ProxyServer : IAsyncLifetime
{
    private WebApplication? _app;

    public ConcurrentQueue<string> Logs { get; } = new();
    public HttpClient Client { get; private set; } = null!;
    public WebApplication App => _app!;

    public async ValueTask InitializeAsync()
    {
        _app = ProxyApp.Build(["--urls", "http://127.0.0.1:0"], builder =>
        {
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Trace);
            builder.Logging.AddProvider(new CapturingLoggerProvider(Logs));
        });
        await _app.StartAsync();
        var address = _app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.First();
        Client = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        if (_app is not null) await _app.DisposeAsync();
    }

    private sealed class CapturingLoggerProvider(ConcurrentQueue<string> logs) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, logs);
        public void Dispose() { }
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<string> logs) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            logs.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {exception}");
    }
}
