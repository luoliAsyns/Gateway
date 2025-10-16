using GatewayService.Services.Coupon;
using GatewayService.Services.ExternalOrder;
using LuoliCommon;
using LuoliCommon.DTO.Agiso;
using LuoliCommon.DTO.ExternalOrder;
using LuoliCommon.Entities;
using LuoliCommon.Enums;
using LuoliCommon.Logger;
using LuoliUtils;
using MethodTimer;
using Microsoft.AspNetCore.Mvc;
using RabbitMQ.Client;
using System.Text;
using System.Text.Json;
using System.Web;
using ThirdApis;
using static CSRedis.CSRedisClient;
using static Grpc.Core.Metadata;
using ILogger = LuoliCommon.Logger.ILogger;

namespace GatewayService.Controllers
{
  

    [Time]
    [Route("api/gateway/receive-external-order")]
    public class ReceiveOrderController : Controller
    {
        private readonly IExternalOrderRepository _externalOrderRepository;
        private readonly ICouponRepository _couponRepository;
        private readonly ILogger _logger;
        private readonly IChannel _channel;
        private readonly AgisoApis _agisoApis;

        public ReceiveOrderController(IExternalOrderRepository orderRepository,
            ICouponRepository couponService, 
            IChannel channel,
            AgisoApis agisoApis,
            ILogger logger)
        {
            _externalOrderRepository = orderRepository;
            _couponRepository = couponService;
            _logger = logger;
            _channel = channel;
            _agisoApis =agisoApis;
            _rabbitMQMsgProps.ContentType = "text/plain";
            _rabbitMQMsgProps.DeliveryMode = DeliveryModes.Persistent;
        }

        private static BasicProperties _rabbitMQMsgProps = new BasicProperties();


        /// <summary>
        /// 这里是客户在(淘宝/闲鱼/..)下单后 收到的
        /// </summary>
        /// <param name="orderCreateDto"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("create")]
        public async Task<ActionResult> ReceiveExternalOrder()
        {
            string rawJson;
            using (var reader = new StreamReader(Request.Body))
            {
                rawJson = await reader.ReadToEndAsync();

                rawJson = rawJson.StartsWith("json=")
                      ? rawJson.Substring("json=".Length)
                      : rawJson;

                rawJson = HttpUtility.UrlDecode(rawJson);
            }

            var (validate, msg, orderCreateDto) = await _agisoApis.ValidateOrderCreateAsync(Request, rawJson);
           
            if (!validate)
            {
                _logger.Error("while ReceiveExternalOrder, not pass ValidateBodyAsync");
                _logger.Error(msg);
                return BadRequest("not pass validate");
            }

            if(!_agisoApis.ValidateSign(orderCreateDto, rawJson, Program.Config.KVPairs["AgisoAppSecret"]))
                return BadRequest("not pass sign validate");
            
            string requestId = HttpContext.TraceIdentifier;

            _logger.Info($"[{requestId}] trigger ReceiveOrderController.ReceiveExternalOrder, fromPlatform:[{orderCreateDto.FromPlatform}] tid: [{orderCreateDto.Tid}]");


            if (await RedisHelper.SIsMemberAsync(RedisKeys.ReceivedExternalOrder, orderCreateDto.Tid))
                return BadRequest("existed in redis already, should be duplicate");

            //OrderCreateRequest 要转成 ExternalOrderDTO
            //ExternalOrderDTO dto = orderCreateDto.ToExternalOrderDTO();
            var (tradeInfoSuccess, tradeInfoDTO ) = await _agisoApis.TradeInfo(Program.Config.KVPairs["AgisoAccessToken"],
                Program.Config.KVPairs["AgisoAppSecret"],
                orderCreateDto.Tid.ToString());

            if (!tradeInfoSuccess)
            {
                string notFound = "not found related order in Agiso";
                Notify(orderCreateDto, notFound);
                _logger.Error(notFound);
                return BadRequest(notFound);
            }

           
            try
            {
                var dtos = tradeInfoDTO!.ToExternalOrderDTO(
                    (order) => {
                        string targetProxy = RedisHelper.HGet(RedisKeys.SkuId2Proxy, order.SkuId);

                        if (!EnumOperator.TryParseIgnoringCaseAndSpaces(targetProxy, out ETargetProxy eTargetProxy))
                            return ETargetProxy.Default;
                        return eTargetProxy;  });

                _logger.Info($"[{requestId}] ReceiveOrderController.ReceiveExternalOrder, convert to ExternalOrderDTO success");

                foreach (var dto in dtos)
                {
                    //把整个dto 丢给rabbit mq
                    await _channel.BasicPublishAsync(exchange: string.Empty,
                       routingKey: RabbitMQKeys.ExternalOrderInserting,
                       true,
                       _rabbitMQMsgProps,
                      Encoding.UTF8.GetBytes(JsonSerializer.Serialize(dto)));
                }
                return Ok("ok");
            }
            catch (Exception ex)
            {
                _logger.Error($"[{requestId}] exception at ReceiveOrderController.ReceiveExternalOrder  {ex.Message}");
                return BadRequest("sent mq failed");
            }
        }

       
        [HttpGet]
        [Route("sse")]
        public async Task BindSSE([FromQuery] string[] coupons)
        {
            Response.ContentType = "text/event-stream";
            Response.Headers.Add("Cache-Control", "no-cache");
            Response.Headers.Add("Connection", "keep-alive");

            var cancellationToken = HttpContext.RequestAborted;

            //因为大概率几秒钟就结束了，所以sse不发心跳
            SubscribeObject subcriber = null;

            // 模拟处理接收到的参数
            _logger.Info($"ReceiveOrderController.BindSSE received coupons: [{string.Join(",", coupons)}]");

            try
            {
                Action<SubscribeMessageEventArgs> receiveMsg = async (finishedCouponMsg) =>
                {

                    string finishedCoupon = finishedCouponMsg.Body;
                    _logger.Info($"ReceiveOrderController.BindSSE receive from Redis:[{finishedCoupon}]");
                    if (!coupons.Contains(finishedCoupon))
                        return;
                    try
                    {
                        string refreshOrderMsg = "data: refresh\n\n";
                        await Response.WriteAsync(refreshOrderMsg);
                        await Response.Body.FlushAsync();
                        _logger.Info($"coupon[{finishedCoupon}] related order is changed; trigger website refresh");
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("while SSE sent refresh signal");
                        _logger.Error(ex.Message);
                    }
                };

                subcriber = RedisHelper.Subscribe((RedisKeys.CouponChanged, receiveMsg));
                await Task.Delay(-1, cancellationToken);
            }
            catch (Exception ex)
            {
                if (subcriber is null)
                    return;
                _logger.Info($"web disconnect SSE，coupons: [{string.Join(",", coupons)}]");
                subcriber.Unsubscribe();
            }

            return;

        }




        private static void Notify(OrderCreateRequest ocRequest, string coreMsg)
        {
            ApiCaller.NotifyAsync(
@$"{Program.Config.ServiceName}.{Program.Config.ServiceId}
msg:{coreMsg}

平台:{ocRequest.FromPlatform}
订单号:{ocRequest.Tid}
金额:{ocRequest.Payment}
销售店铺旺旺:{ocRequest.SellerNick}
订单状态:{ocRequest.Status}", Program.NotifyUsers);
        }


    }

}
