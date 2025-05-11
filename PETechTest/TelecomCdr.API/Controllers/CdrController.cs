using Microsoft.AspNetCore.Mvc;

namespace TelecomCdr.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class CdrController : ControllerBase
    {
        private readonly ILogger<CdrController> _logger;

        public CdrController(ILogger<CdrController> logger)
        {
            _logger = logger;
        }
    }
}
