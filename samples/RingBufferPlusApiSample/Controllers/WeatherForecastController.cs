// ***************************************************************************************
// MIT LICENCE
// The maintenance and evolution is maintained by the RingBufferPlus project under MIT license
// ***************************************************************************************

using Microsoft.AspNetCore.Mvc;
using RingBufferPlus;

namespace RingBufferPlusApiSample.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class WeatherForecastController(IRingBufferService<int> ringBufferService) : ControllerBase
    {
        private static readonly string[] Summaries =
        [
            "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
        ];

        private readonly IRingBufferService<int> _ringBufferService = ringBufferService;
        private static bool _toInvalidate = true;

        [HttpGet(Name = "GetWeatherForecast")]
        public async Task<IEnumerable<WeatherForecast>> Get(CancellationToken token)
        {

            await using (var buffer = await _ringBufferService.AcquireAsync(token))
            {
                _toInvalidate = !_toInvalidate;
                if (_toInvalidate)
                {
                    buffer.Invalidate();
                }
                await Task.Delay(100, token);
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
        public async Task<ActionResult> ChangeCapacity(ScaleSwitch scaleUnit)
        {
            // This buffer is registered as the base IRingBufferService<int> (ADR007: manual switching
            // is not part of that type). This buffer was built via ElasticCapacity without
            // AutoScaleAcquireFault, so it does support manual switching at runtime - opt back in
            // explicitly rather than casting blindly.
            if (_ringBufferService is not IRingBufferManualScaleService<int> manualScaleService)
            {
                return BadRequest("Manual scale switching is not available for this buffer.");
            }
            await manualScaleService.SwitchToAsync(scaleUnit);
            return Ok(_ringBufferService.CurrentCapacity);
        }
    }
}
