using Cohesive.MaterializationHarness.Materialize;

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
                await controller.RunCurrentAttemptAsync(stoppingToken);
            }
            catch (MaterializationHarnessRunSuspendedException exception)
            {
                logger.LogInformation("Materialization attempt suspended at a page boundary: {Reason}", exception.Message);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException exception)
            {
                logger.LogInformation("Materialization attempt stopped: {Reason}", exception.Message);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Materialization attempt failed and remains resumable.");
            }

            await controller.WaitForWorkAsync(stoppingToken);
        }
    }
}
