﻿using Microsoft.AspNetCore.Mvc;
using TelecomCdr.Abstraction.Interfaces.Repository;
using TelecomCdr.Domain;

namespace TelecomCdr.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class JobStatusController : ControllerBase
    {
        private readonly IJobStatusRepository _jobStatusRepository;
        private readonly ILogger<JobStatusController> _logger;

        public JobStatusController(IJobStatusRepository jobStatusRepository, ILogger<JobStatusController> logger)
        {
            _jobStatusRepository = jobStatusRepository;
            _logger = logger;
        }

        [HttpGet("{correlationId}")]
        [ProducesResponseType(typeof(JobStatus), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetJobStatus(Guid correlationId)
        {
            if (correlationId != Guid.Empty)
            {
                return BadRequest("Correlation ID cannot be empty.");
            }

            _logger.LogInformation("Fetching job status for Correlation ID: {CorrelationId}", correlationId);

            var jobStatus = await _jobStatusRepository.GetJobStatusByCorrelationIdAsync(correlationId);

            if (jobStatus == null)
            {
                _logger.LogWarning("Job status not found for Correlation ID: {CorrelationId}", correlationId);
                return NotFound($"No job status found for Correlation ID: {correlationId}");
            }

            return Ok(jobStatus);
        }
    }
}
