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
                    // Discards this item instead of returning it to the pool - e.g. after finding
                    // it unhealthy. A replacement is created automatically. This sample alternates
                    // it just to show the path.
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
            // This buffer is injected as the base IRingBufferService<int>, which does not expose
            // manual switching. Every elastic buffer still supports it at runtime - cast to
            // IRingBufferManualScaleService<int> to opt back in explicitly.
            if (_ringBufferService is not IRingBufferManualScaleService<int> manualScaleService)
            {
                return BadRequest("Manual scale switching is not available for this buffer.");
            }
            await manualScaleService.SwitchToAsync(scaleUnit, TimeSpan.FromMinutes(1));
            return Ok(_ringBufferService.CurrentCapacity);
        }
    }
}
