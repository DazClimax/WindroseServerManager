using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WindroseServerManager.Core.Services;

public sealed class ServerWatchdogService(
    ILogger<ServerWatchdogService> logger,
    IServerProcessService server,
    IAppSettingsService settings) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var initialDelay = TimeSpan.FromSeconds(10 + Math.Clamp(settings.Current.AutoStartDelaySeconds, 0, 60));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(initialDelay, stoppingToken).ConfigureAwait(false);
                initialDelay = TimeSpan.FromSeconds(10);

                if (!settings.Current.AutoRestartOnCrash)
                    continue;

                await server.RunWatchdogCheckAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Server watchdog check failed");
            }
        }
    }
}
