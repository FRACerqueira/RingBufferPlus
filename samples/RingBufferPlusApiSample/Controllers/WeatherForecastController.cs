using Microsoft.AspNetCore.Mvc;
using RingBufferPlus;

namespace RingBufferPlusApiSample.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class WeatherForecastController(IRingBufferService<int> ringBufferService, ILogger<WeatherForecastController> logger) : ControllerBase
    {
        private static readonly string[] Summaries =
        [
            "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
        ];

        private readonly ILogger<WeatherForecastController> _logger = logger;
        private readonly IRingBufferService<int> _ringBufferService = ringBufferService;
        private static bool _toInvalidade = true;

        [HttpGet(Name = "GetWeatherForecast")]
        public IEnumerable<WeatherForecast> Get(CancellationToken token)
        {

            using (var buffer = _ringBufferService.Accquire(token))
            {
                _toInvalidade = !_toInvalidade;
                if (_toInvalidade)
                {
                    buffer.Invalidate();
                }
                token.WaitHandle.WaitOne(100);
            }
            return Enumerable.Range(1, 5).Select(index => new WeatherForecast
            {
                Date = DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
                TemperatureC = Random.Shared.Next(-20, 55),
                Summary = Summaries[Random.Shared.Next(Summaries.Length)]
            })
            .ToArray();
        }

        [HttpPatch]
        [Route("/ChangeCapacity")]
        public ActionResult ChangeCapacity(ScaleSwith scaleUnit)
        {
            _ringBufferService.SwithTo(scaleUnit);
            return Ok();
        }
    }
}
