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
        private static bool _toInvalidade = true;

        [HttpGet(Name = "GetWeatherForecast")]
        public async Task<IEnumerable<WeatherForecast>> Get(CancellationToken token)
        {

            using (var buffer = await  _ringBufferService.AcquireAsync(token))
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
        public async Task<ActionResult> ChangeCapacity(ScaleSwitch scaleUnit)
        {
            await _ringBufferService.SwitchToAsync(scaleUnit);
            return Ok(_ringBufferService.CurrentCapacity);
        }
    }
}
