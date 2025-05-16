using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask.Client;
using TelecomCdr.Domain;

namespace TelecomCdr.DurableFunctions.Activities
{
    public class GetJobStatusActivityFunction
    {
        [Function(nameof(GetJobStatusActivityFunction))]
        public async Task<JobStatus> Run([ActivityTrigger] string correlationId, [DurableClient] DurableTaskClient client /* IJobStatusRepository via DI */)
        { 
            /* Fetch from repo */
            return new JobStatus();
        }
    }
}
