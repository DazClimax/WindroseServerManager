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
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);

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
