using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using TelecomCdr.Abstraction.Interfaces.Repository;
using TelecomCdr.DurableFunctions.Dtos;

namespace TelecomCdr.DurableFunctions.Activities
{
    public class UpdateJobStatusActivityFunction
    {
        [Function(nameof(UpdateJobStatusActivityFunction))]
        public async Task Run([ActivityTrigger] JobStatusActivityInput statusInput, ILogger<UpdateJobStatusActivityFunction> logger, IJobStatusRepository jobStatusRepository)
        {
            /* ... use jobStatusRepository.UpdateJobStatusAsync ... */
        }
    }
}
