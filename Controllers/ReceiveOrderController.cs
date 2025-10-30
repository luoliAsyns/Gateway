﻿using GatewayService.Services.Coupon;
using GatewayService.Services.ExternalOrder;
using LuoliCommon;
using LuoliCommon.DTO.Agiso;
using LuoliCommon.DTO.ConsumeInfo;
using LuoliCommon.DTO.Coupon;
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
  

    [Route("api/gateway/prod")]
    public class ReceiveOrderController : Controller
    {
        private readonly IExternalOrderRepository _externalOrderRepository;
        private readonly ICouponRepository _couponRepository;
        private readonly ILogger _logger;
        private readonly IChannel _channel;
        private readonly AgisoApis _agisoApis;
        private readonly AsynsApis _asynsApis;

        public ReceiveOrderController(IExternalOrderRepository orderRepository,
            ICouponRepository couponService, 
            IChannel channel,
            AgisoApis agisoApis,
            ILogger logger, AsynsApis asynsApis)
        {
            _asynsApis = asynsApis;
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
        [Route("agiso-pull")]
        [Time]
        public async Task<ActionResult> ReceiveExternalOrder()
        {
            long timestamp = Request.Query.ContainsKey("timestamp") ? long.Parse(Request.Query["timestamp"]) : 0;
            long aopic= Request.Query.ContainsKey("aopic") ? long.Parse(Request.Query["aopic"]) : 0;
            string sign= Request.Query.ContainsKey("sign") ? Request.Query["sign"].ToString() : string.Empty;
            string fromPlatform= Request.Query.ContainsKey("fromPlatform") ? Request.Query["fromPlatform"].ToString() : string.Empty;

            _logger.Info($"received: fromPlatform:{fromPlatform}, timestamp:{timestamp}, aopic:{aopic}, sign:{sign}");

            if (aopic == 2097152)
            {
                _logger.Info("receive agiso-pull 买家付款推送");
                return await createExternalOrder();
            }
            else if (aopic == 256)
            {
                _logger.Info("receive agiso-pull 退款创建推送");
                return await refundExternalOrder();
            }
            else 
            {
                _logger.Warn($"receive agiso-pull, but unknown aopic[{aopic}]");
                return BadRequest("unknown aopic");
            }
        }

        private async Task<ActionResult> createExternalOrder()
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

            _logger.Debug($"rawJson: {rawJson}");

            var (validate, msg, orderCreateDto) = await _agisoApis.ValidateOrderCreateAsync(Request, rawJson);

            if (!validate)
            {
                _logger.Error("while ReceiveExternalOrder, not pass ValidateBodyAsync");
                _logger.Error(msg);
                return BadRequest("not pass validate");
            }

            if (!_agisoApis.ValidateSign(orderCreateDto, rawJson, Program.Config.KVPairs["AgisoAppSecret"]))
                return BadRequest("not pass sign validate");

            string requestId = HttpContext.TraceIdentifier;

            _logger.Info($"[{requestId}] trigger ReceiveOrderController.ReceiveExternalOrder, fromPlatform:[{orderCreateDto.FromPlatform}] tid: [{orderCreateDto.Tid}]");

            if (await RedisHelper.SIsMemberAsync(RedisKeys.ReceivedExternalOrder, orderCreateDto.Tid))
                return BadRequest("existed in redis already, should be duplicate");

            //OrderCreateRequest 要转成 ExternalOrderDTO
            //ExternalOrderDTO dto = orderCreateDto.ToExternalOrderDTO();
            var (tradeInfoSuccess, tradeInfoDTO) = await _agisoApis.TradeInfo(Program.Config.KVPairs["AgisoAccessToken"],
                Program.Config.KVPairs["AgisoAppSecret"],
                orderCreateDto.Tid.ToString());

            if (!tradeInfoSuccess)
            {
                string notFound = "not found related order in Agiso";
                Notify(orderCreateDto, notFound);
                _logger.Error(notFound);
                return BadRequest(notFound);
            }

            //System.IO.File.WriteAllText("4835278155226513614_TradeInfo", JsonSerializer.Serialize(tradeInfoDTO));

            try
            {
                var dto = tradeInfoDTO.ToExternalOrderDTO(
                    (order) =>
                    {
                        string targetProxy = RedisHelper.HGet(RedisKeys.SkuId2Proxy, order.SkuId);

                        if (!EnumHandler.TryParseIgnoringCaseAndSpaces(targetProxy, out ETargetProxy eTargetProxy))
                            return ETargetProxy.Default;
                        return eTargetProxy;
                    });

                _logger.Info($"[{requestId}] ReceiveOrderController.ReceiveExternalOrder, convert to ExternalOrderDTO success");


                //把整个dto 丢给rabbit mq
                await _channel.BasicPublishAsync(exchange: string.Empty,
                   routingKey: Program.Config.KVPairs["StartWith"] +  RabbitMQKeys.ExternalOrderInserting,
                   true,
                   _rabbitMQMsgProps,
                  Encoding.UTF8.GetBytes(JsonSerializer.Serialize(dto)));

                return Ok("ok");

            }
            catch (Exception ex)
            {
                _logger.Error($"[{requestId}] exception at ReceiveOrderController.ReceiveExternalOrder  {ex.Message}");
                return BadRequest("sent mq failed");
            }
        }

        private async Task<ActionResult> refundExternalOrder()
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

            _logger.Debug($"rawJson: {rawJson}");

            var (validate, msg, orderRefundDto) = await _agisoApis.ValidateOrderRefundAsync(Request, rawJson);

            if (!validate)
            {
                _logger.Error("while ReceiveExternalOrder, not pass ValidateBodyAsync");
                _logger.Error(msg);
                return BadRequest("not pass validate");
            }

            if (!_agisoApis.ValidateSign(orderRefundDto, rawJson, Program.Config.KVPairs["AgisoAppSecret"]))
                return BadRequest("not pass sign validate");

            string requestId = HttpContext.TraceIdentifier;

            _logger.Info($"[{requestId}] trigger ReceiveOrderController.ReceiveExternalOrder, fromPlatform:[{orderRefundDto.FromPlatform}] tid: [{orderRefundDto.Tid}]");

          

            try
            {
                var eoResp = await _externalOrderRepository.Get(orderRefundDto.FromPlatform, orderRefundDto.Tid.ToString());
                if(!eoResp.ok || (eoResp.data is null ))
                {
                    _logger.Warn($"[{requestId}] ReceiveOrderController.ReceiveExternalOrder, not found related ExternalOrderDTO with fromPlatform:[{orderRefundDto.FromPlatform}] tid: [{orderRefundDto.Tid}]");
                    return BadRequest("not found ExternalOrderDTO");
                }
              
                var updateEOResp = await _externalOrderRepository.Update(new LuoliCommon.DTO.ExternalOrder.UpdateRequest()
                {
                     EO = eoResp.data,
                     Event = EEvent.Received_Refund_EO
                });

                if (!updateEOResp.ok)
                {
                    _logger.Warn($"[{requestId}] ReceiveOrderController.ReceiveExternalOrder, update ExternalOrderDTO failed with fromPlatform:[{orderRefundDto.FromPlatform}] tid: [{orderRefundDto.Tid}]");
                    return BadRequest("update ExternalOrderDTO failed");
                }

                var updateCouponResp = await _couponRepository.Update(new LuoliCommon.DTO.Coupon.UpdateRequest()
                {
                    Coupon = await _couponRepository.Query(eoResp.data.FromPlatform, eoResp.data.Tid).ContinueWith(t => t.Result.data),
                    Event = EEvent.Received_Refund_EO
                });

                if (!updateCouponResp.ok)
                {
                    _logger.Warn($"[{requestId}] ReceiveOrderController.ReceiveExternalOrder, update CouponDTO failed with fromPlatform:[{orderRefundDto.FromPlatform}] tid: [{orderRefundDto.Tid}]");
                    return BadRequest("update CouponDTO failed");
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
        [Route("refresh-sse")]
        [Time]
        public async Task BindSSE([FromQuery] string coupon)
        {
            Response.ContentType = "text/event-stream";
            Response.Headers.Add("Cache-Control", "no-cache");
            Response.Headers.Add("Connection", "keep-alive");

            var cancellationToken = HttpContext.RequestAborted;

            //因为大概率几秒钟就结束了，所以sse不发心跳
            SubscribeObject subcriber = null;

            // 模拟处理接收到的参数
            _logger.Info($"ReceiveOrderController.BindSSE received coupon: [{coupon}]");

            try
            {
                Action<SubscribeMessageEventArgs> receiveMsg = async (finishedCouponMsg) =>
                {

                    string finishedCoupon = finishedCouponMsg.Body;
                    if (coupon!=finishedCoupon)
                        return;

                    _logger.Info($"ReceiveOrderController.BindSSE receive from Redis:[{finishedCoupon}]");

                    try
                    {
                        string refreshOrderMsg = "data: refresh\n\n";
                        await Response.WriteAsync(refreshOrderMsg);
                        await Response.Body.FlushAsync();
                        _logger.Info($"coupon[{finishedCoupon}] related order is changed; trigger website refresh");
                        HttpContext.RequestAborted.ThrowIfCancellationRequested();
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("while SSE sent refresh signal");
                        _logger.Error(ex.Message);
                    }
                };

                subcriber = RedisHelper.Subscribe((RedisKeys.Pub_RefreshPlaceOrderStatus, receiveMsg));
                await Task.Delay(-1, cancellationToken);
            }
            catch (Exception ex)
            {
                if (subcriber is null)
                    return;
                _logger.Info($"web disconnect SSE，coupon: [{coupon}]");
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

        [HttpGet]
        [Route("coupon")]
        [Time]
        public async Task<ApiResponse<CouponDTO>> QueryCoupon(string coupon)
        {
            _logger.Info($"trigger ReceiveOrder.QueryCoupon with coupon[{coupon}]");

            return await _couponRepository.Query(coupon);
        }


        [HttpGet]
        [Route("consume-info")]
        [Time]
        public async Task<ApiResponse<ConsumeInfoDTO>> QueryConsumeInfo(string goodsType, string coupon)
        {
            _logger.Info($"trigger ReceiveOrder.QueryConsumeInfo with goodsType[{goodsType}],coupon[{coupon}]");

            return await _asynsApis.ConsumeInfoQuery(goodsType,coupon);
        }
    }

}
