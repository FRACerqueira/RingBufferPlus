using Microsoft.AspNetCore.Mvc;
using RabbitMQ.Client;
using RingBufferPlus;
using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DotNetProbes.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class PublisherController : ControllerBase
    {
        private readonly IRunningRingBuffer<IModel> _runningRingBuffer;

        public PublisherController(IRunningRingBuffer<IModel> runningRingBuffer)
        {
            if (_runningRingBuffer == null)
            {
                _runningRingBuffer = runningRingBuffer;
            }
        }

        [HttpGet(Name = "GetPublisher")]
        [ProducesResponseType((int)HttpStatusCode.OK)]
        [ProducesResponseType((int)HttpStatusCode.InternalServerError)]
        public async Task<ActionResult> Get(CancellationToken cancelatiotoken)
        {
            var messageBodyBytes = Encoding.UTF8.GetBytes("Hello World!");
            using (var ctx = _runningRingBuffer.Accquire(cancellation: cancelatiotoken))
            {
                if (ctx.SucceededAccquire)
                {
                    try
                    {
                        IBasicProperties props = ctx.Current.CreateBasicProperties();
                        props.ContentType = "text/plain";
                        props.DeliveryMode = 2;
                        props.Expiration = "10000";
                        ctx.Current.BasicPublish(exchange: "",
                            routingKey: "RingBufferTest",
                            mandatory: true,
                            basicProperties: props,
                            body: messageBodyBytes);
                        ctx.Current.WaitForConfirmsOrDie();
                        return await Task.FromResult(Ok());
                    }
                    catch (Exception ex)
                    {
                        ctx.Invalidate(ex);
                    }
                }
                return await Task.FromResult(StatusCode(500));
            }
        }

    }
}
