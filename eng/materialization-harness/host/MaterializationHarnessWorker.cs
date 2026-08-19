namespace Cohesive.MaterializationHarness.Host;

sealed class MaterializationHarnessWorker(
    MaterializationHarnessExecutionController controller,
    ILogger<MaterializationHarnessWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await controller.RunReadyProcessesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException exception)
            {
                logger.LogInformation("Materialization Process work stopped at a durable boundary: {Reason}", exception.Message);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Materialization Process work failed and remains resumable.");
            }

            await controller.WaitForWorkAsync(stoppingToken);
        }
    }
}
