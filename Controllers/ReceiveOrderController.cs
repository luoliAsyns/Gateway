using LuoliCommon;
using LuoliCommon.DTO.Admin;
using LuoliCommon.DTO.Agiso;
using LuoliCommon.DTO.ConsumeInfo;
using LuoliCommon.DTO.ConsumeInfo.Sexytea;
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
using System.Text.RegularExpressions;
using System.Web;
using ThirdApis;
using ThirdApis.Services.ConsumeInfo;
using ThirdApis.Services.Coupon;
using ThirdApis.Services.ExternalOrder;
using static CSRedis.CSRedisClient;
using static GatewayService.Controllers.SexyteaController;
using static Grpc.Core.Metadata;
using ILogger = LuoliCommon.Logger.ILogger;

namespace GatewayService.Controllers
{


    [Route("api/gateway/prod")]
    public class ReceiveOrderController : Controller
    {

        private readonly IExternalOrderRepository _externalOrderRepository;
        private readonly ICouponRepository _couponRepository;
        private readonly IConsumeInfoRepository _consumeInfoRepository;

        private readonly ILogger _logger;
        private readonly IChannel _channel;
        private readonly AgisoApis _agisoApis;

        private readonly SexyteaApis _sexyteaApis;

        public ReceiveOrderController(
            IExternalOrderRepository orderRepository,
            ICouponRepository couponService,
            IConsumeInfoRepository consumeInfoRepository,
            IChannel channel,
            AgisoApis agisoApis,
            SexyteaApis sexyteaApis,
            ILogger logger)
        {
            _externalOrderRepository = orderRepository;
            _couponRepository = couponService;
            _consumeInfoRepository = consumeInfoRepository;

            _logger = logger;
            _channel = channel;

            _agisoApis = agisoApis;
            _sexyteaApis = sexyteaApis;
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
            long aopic = Request.Query.ContainsKey("aopic") ? long.Parse(Request.Query["aopic"]) : 0;
            string sign = Request.Query.ContainsKey("sign") ? Request.Query["sign"].ToString() : string.Empty;
            string fromPlatform = Request.Query.ContainsKey("fromPlatform") ? Request.Query["fromPlatform"].ToString() : string.Empty;

            _logger.Info($"received: fromPlatform:{fromPlatform}, timestamp:{timestamp}, aopic:{aopic}, sign:{sign}");

            switch (aopic)
            {
                case 1:
                    _logger.Info("receive agiso-pull 闲鱼买家付款推送");
                    return await createXianyuExternalOrder();
                case 8:
                    _logger.Info("receive agiso-pull 闲鱼退款创建推送");
                    return await refundXianyuExternalOrder();
                case 2097152l:
                    _logger.Info("receive agiso-pull 淘宝买家付款推送");
                    return await createTaobaoExternalOrder();
                case 256l:
                    _logger.Info("receive agiso-pull 淘宝退款创建推送");
                    return await refundTaobaoExternalOrder();


                default:
                    _logger.Warn($"receive agiso-pull, but unknown aopic[{aopic}]");
                    return BadRequest("unknown aopic");
            }
        }
            




        /// <summary>
        /// 淘宝订单创建
        /// </summary>
        /// <returns></returns>
        private async Task<ActionResult> createTaobaoExternalOrder()
        {

            string rawJson;
            using (var reader = new StreamReader(Request.Body))
            {
                rawJson = await reader.ReadToEndAsync();
                rawJson = HttpUtility.UrlDecode(rawJson);

                _logger.Debug($"rawJson before regex: {rawJson}");

                var match = Regex.Match(rawJson, @"\{.*?\}", RegexOptions.Singleline);
                if (match.Success)
                    rawJson = match.Value;
                else
                    _logger.Error("正则提取失败了");
            }
            _logger.Debug($"rawJson after regex: {rawJson}");


            var (validate, msg, orderCreateDto) = await _agisoApis.ValidateTBOrderCreateAsync(Request, rawJson);

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


            var accessToken = await RedisHelper.HGetAsync(RedisKeys.AgisoAccessToken, orderCreateDto.SellerNick);

            if(accessToken is null)
            {
                string notFound = $"店铺id[{orderCreateDto.SellerNick}]没有找到对应的agiso access token";
                Notify(orderCreateDto, notFound);
                _logger.Error(notFound);
                return BadRequest(notFound);
            }

            //OrderCreateRequest 要转成 ExternalOrderDTO
            var (tradeInfoSuccess, tradeInfoDTO) = await _agisoApis.TradeInfo(accessToken,
                Program.Config.KVPairs["AgisoAppSecret"],
                orderCreateDto.Tid.ToString());
            
            if (!tradeInfoSuccess)
            {
                string notFound = "not found related order in Agiso";
                Notify(orderCreateDto, notFound);
                _logger.Error(notFound);
                return BadRequest(notFound);
            }

            _logger.Info($"[{requestId}] ReceiveOrderController.ReceiveExternalOrder, override TradeInfo status[{tradeInfoDTO.Data.Status}] from [{orderCreateDto.Status}]");

            tradeInfoDTO.Data.Status = orderCreateDto.Status; //用最新的状态覆盖

            _logger.Info($"[{requestId}] ReceiveOrderController.ReceiveExternalOrder, get TradeInfo success");

            try
            {
                var dto = tradeInfoDTO.ToExternalOrderDTO(
                    (order) =>
                    {
                        try
                        {
                            SkuIdMapItem item = RedisHelper.HGet<SkuIdMapItem>(RedisKeys.SkuId2Proxy, order.SkuId);
                            return item;
                        }
                        catch
                        {

                        }
                        return null;
                        
                    });


                if(dto is null)
                {
                    _logger.Warn($"[{requestId}] ReceiveOrderController.ReceiveExternalOrder, convert to ExternalOrderDTO failed, skuid[{tradeInfoDTO.Data.Orders.First().SkuId}]");
                    return Ok($"可能是其他sku[{tradeInfoDTO.Data.Orders.First().SkuId}]");
                }

                _logger.Info($"[{requestId}] ReceiveOrderController.ReceiveExternalOrder, convert to ExternalOrderDTO success");


                //把整个dto 丢给rabbit mq
                await _channel.BasicPublishAsync(exchange: string.Empty,
                   routingKey: Program.Config.KVPairs["StartWith"] +  RabbitMQKeys.ExternalOrderInserting,
                   true,
                   _rabbitMQMsgProps,
                  Encoding.UTF8.GetBytes(JsonSerializer.Serialize(dto)));

                RedisHelper.IncrByAsync(RedisKeys.Prom_ReceivedOrders);

                return Ok("ok");

            }
            catch (Exception ex)
            {
                _logger.Error($"[{requestId}] exception at ReceiveOrderController.ReceiveExternalOrder  {ex.Message}");
                return BadRequest("sent mq failed");
            }
        }


        /// <summary>
        /// 闲鱼订单创建
        /// </summary>
        /// <returns></returns>
        private async Task<ActionResult> createXianyuExternalOrder()
        {

            string rawJson;
            using (var reader = new StreamReader(Request.Body))
            {
                rawJson = await reader.ReadToEndAsync();
                rawJson = HttpUtility.UrlDecode(rawJson);

                _logger.Debug($"rawJson before regex: {rawJson}");

                var match = Regex.Match(rawJson, @"\{.*?\}", RegexOptions.Singleline);
                if (match.Success)
                    rawJson = match.Value;
                else
                    _logger.Error("正则提取失败了");
            }
            _logger.Debug($"rawJson after regex: {rawJson}");


            var (validate, msg, orderCreateDto) = await _agisoApis.ValidateXYOrderCreateAsync(Request, rawJson);

            if (!validate)
            {
                _logger.Error("while ReceiveExternalOrder, not pass ValidateBodyAsync");
                _logger.Error(msg);
                return BadRequest("not pass validate");
            }

            if (!_agisoApis.ValidateSign(orderCreateDto, rawJson, Program.Config.KVPairs["AgisoAppSecret"]))
                return BadRequest("not pass sign validate");

            //因为闲鱼里可能有其他产品
            if (!await RedisHelper.HExistsAsync(RedisKeys.SkuId2Proxy, orderCreateDto.item_id))
                return Ok($"XY item_id[{orderCreateDto.item_id}] not found in sku map, ignore");

            string requestId = HttpContext.TraceIdentifier;

            _logger.Info($"[{requestId}] trigger ReceiveOrderController.ReceiveExternalOrder, fromPlatform:[{orderCreateDto.FromPlatform}] tid: [{orderCreateDto.biz_order_id}]");


            var accessToken = await RedisHelper.HGetAsync(RedisKeys.AgisoAccessToken, orderCreateDto.seller_id.ToString());

            if (accessToken is null)
            {
                string notFound = $"闲鱼店铺id[{orderCreateDto.seller_id}]没有找到对应的agiso access token";
                Notify(orderCreateDto, notFound);
                _logger.Error(notFound);
                return BadRequest(notFound);
            }

            //OrderCreateRequest 要转成 ExternalOrderDTO
            var (tradeInfoSuccess, tradeInfoDTO) = await _agisoApis.XYTradeInfo(accessToken,
                Program.Config.KVPairs["AgisoAppSecret"],
                orderCreateDto.biz_order_id.ToString());

            if (!tradeInfoSuccess)
            {
                string notFound = "not found related order in Agiso";
                Notify(orderCreateDto, notFound);
                _logger.Error(notFound);
                return BadRequest(notFound);
            }

            _logger.Info($"[{requestId}] ReceiveOrderController.ReceiveExternalOrder, get TradeInfo success");

            try
            {
                var dto = tradeInfoDTO.ToExternalOrderDTO(
                    (xyTradeData) =>
                    {
                        try
                        {
                            SkuIdMapItem item = RedisHelper.HGet<SkuIdMapItem>(RedisKeys.SkuId2Proxy, xyTradeData.sku.ToString());
                            return item;
                        }
                        catch
                        {

                        }
                        return null;

                    });


                if (dto is null)
                {
                    _logger.Warn($"[{requestId}] ReceiveOrderController.ReceiveExternalOrder, convert to ExternalOrderDTO failed, skuid[{tradeInfoDTO.Data.sku}]");
                    return Ok($"可能是其他sku[{tradeInfoDTO.Data.sku}]");
                }
                //使用长id代替nick name
                dto.SellerNick = orderCreateDto.seller_id.ToString();

                _logger.Info($"[{requestId}] ReceiveOrderController.ReceiveExternalOrder, convert to ExternalOrderDTO success");


                //把整个dto 丢给rabbit mq
                await _channel.BasicPublishAsync(exchange: string.Empty,
                   routingKey: Program.Config.KVPairs["StartWith"] + RabbitMQKeys.ExternalOrderInserting,
                   true,
                   _rabbitMQMsgProps,
                  Encoding.UTF8.GetBytes(JsonSerializer.Serialize(dto)));

                RedisHelper.IncrByAsync(RedisKeys.Prom_ReceivedOrders);

                return Ok("ok");

            }
            catch (Exception ex)
            {
                _logger.Error($"[{requestId}] exception at ReceiveOrderController.ReceiveExternalOrder  {ex.Message}");
                return BadRequest("sent mq failed");
            }
        }



        /// <summary>
        /// 淘宝订单退款
        /// </summary>
        /// <returns></returns>
        private async Task<ActionResult> refundTaobaoExternalOrder()
        {

            string rawJson;
            using (var reader = new StreamReader(Request.Body))
            {
                rawJson = await reader.ReadToEndAsync();
                rawJson = HttpUtility.UrlDecode(rawJson);

                _logger.Debug($"rawJson before regex: {rawJson}");

                var match = Regex.Match(rawJson, @"\{.*?\}", RegexOptions.Singleline);
                if (match.Success)
                    rawJson = match.Value;
                else
                    _logger.Error("正则提取失败了");

            }
            _logger.Debug($"rawJson after regex: {rawJson}");

            var (validate, msg, orderRefundDto) = await _agisoApis.ValidateTBOrderRefundAsync(Request, rawJson);

            if (!validate)
            {
                _logger.Error("while ReceiveExternalOrder, not pass ValidateBodyAsync");
                _logger.Error(msg);
                return BadRequest("not pass validate");
            }

            if (!_agisoApis.ValidateSign(orderRefundDto, rawJson, Program.Config.KVPairs["AgisoAppSecret"]))
                return BadRequest("not pass sign validate");

            string requestId = HttpContext.TraceIdentifier;

            _logger.Info($"[{requestId}] trigger ReceiveOrderController.ReceiveExternalOrder, fromPlatform:[{orderRefundDto.Platform}] tid: [{orderRefundDto.Tid}]");

            try
            {

                var eoResp = await _externalOrderRepository.Get(orderRefundDto.Platform, orderRefundDto.Tid.ToString());
                if(!eoResp.ok || (eoResp.data is null ))
                {
                    _logger.Warn($"[{requestId}] ReceiveOrderController.ReceiveExternalOrder, not found related ExternalOrderDTO with fromPlatform:[{orderRefundDto.Platform}] tid: [{orderRefundDto.Tid}]");
                    return Ok("not found ExternalOrderDTO, 可能是店里其他产品的退款");
                }
              
                

                var updateEOResp = await _externalOrderRepository.Update(new LuoliCommon.DTO.ExternalOrder.UpdateRequest()
                {
                     EO = eoResp.data,
                     Event = EEvent.Received_Refund_EO
                });

                if (!updateEOResp.ok)
                {
                    _logger.Warn($"[{requestId}] ReceiveOrderController.ReceiveExternalOrder, update ExternalOrderDTO failed with fromPlatform:[{orderRefundDto.Platform}] tid: [{orderRefundDto.Tid}]");
                    return BadRequest("update ExternalOrderDTO failed");
                }


                //如果已经消费了
                //卡密更新会失败
                //所以前移统计
                RedisHelper.IncrByAsync(RedisKeys.Prom_ReceivedRefund);

                var coupon = await _couponRepository.Query(eoResp.data.FromPlatform, eoResp.data.Tid).ContinueWith(t => t.Result.data);

                //卡密未消费，作废掉
                if(coupon.Status == ECouponStatus.Shipped)
                {
                    var updateCouponResp = await _couponRepository.Update(new LuoliCommon.DTO.Coupon.UpdateRequest()
                    {
                        Coupon = coupon,
                        Event = EEvent.Received_Refund_EO
                    });

                    if (!updateCouponResp.ok)
                    {
                        _logger.Warn($"[{requestId}] ReceiveOrderController.ReceiveExternalOrder, update CouponDTO failed with fromPlatform:[{orderRefundDto.Platform}] tid: [{orderRefundDto.Tid}]");
                        return BadRequest("update CouponDTO failed 可能是已经消费过了");
                    }
                }
                //卡密已消费
                else if (coupon.Status == ECouponStatus.Consumed)
                {
                    //茶颜的卡密已消费，需要尝试一下代理退款
                    if(eoResp.data.TargetProxy == ETargetProxy.sexytea)
                    {
                        _logger.Info($"sexytea coupon[{coupon.Coupon}] consumed, have a try to refund");

                        var result = await ApiCaller.PostAsync("http://localhost:8080/api/gateway/admin/sexytea/refund", JsonSerializer.Serialize(
                            new sexyteaRefundReq() { Coupon = coupon.Coupon, OrderNo = coupon.ProxyOrderId}));

                        return Ok("ok, 已经消费，触发后台退款流程" + await result.Content.ReadAsStringAsync());
                    }
                  
                }
                else
                {
                    return BadRequest($"coupon status [{coupon.Status.ToString()}]  不处理");
                }

                return Ok("ok");

            }
            catch (Exception ex)
            {
                _logger.Error($"[{requestId}] exception at ReceiveOrderController.ReceiveExternalOrder  {ex.Message}");
                return BadRequest("sent mq failed");
            }
        }

        /// <summary>
        /// 闲鱼订单退款
        /// </summary>
        /// <returns></returns>
        private async Task<ActionResult> refundXianyuExternalOrder()
        {

            string rawJson;
            using (var reader = new StreamReader(Request.Body))
            {
                rawJson = await reader.ReadToEndAsync();
                rawJson = HttpUtility.UrlDecode(rawJson);

                _logger.Debug($"rawJson before regex: {rawJson}");

                var match = Regex.Match(rawJson, @"\{.*?\}", RegexOptions.Singleline);
                if (match.Success)
                    rawJson = match.Value;
                else
                    _logger.Error("正则提取失败了");

            }
            _logger.Debug($"rawJson after regex: {rawJson}");

            var (validate, msg, orderRefundDto) = await _agisoApis.ValidateXYOrderRefundAsync(Request, rawJson);

            if (!validate)
            {
                _logger.Error("while ReceiveExternalOrder, not pass ValidateBodyAsync");
                _logger.Error(msg);
                return BadRequest("not pass validate");
            }

            if (!_agisoApis.ValidateSign(orderRefundDto, rawJson, Program.Config.KVPairs["AgisoAppSecret"]))
                return BadRequest("not pass sign validate");


            //因为闲鱼里可能有其他产品
            if (!await RedisHelper.HExistsAsync(RedisKeys.SkuId2Proxy, orderRefundDto.item_id))
                return Ok($"XY item_id[{orderRefundDto.item_id}] not found in sku map, ignore");


            string requestId = HttpContext.TraceIdentifier;

            _logger.Info($"[{requestId}] trigger ReceiveOrderController.ReceiveExternalOrder, fromPlatform:[XIANYU] tid: [{orderRefundDto.biz_order_id}]");

            try
            {

                var eoResp = await _externalOrderRepository.Get("XIANYU", orderRefundDto.biz_order_id.ToString());
                if (!eoResp.ok || (eoResp.data is null))
                {
                    _logger.Warn($"[{requestId}] ReceiveOrderController.ReceiveExternalOrder, not found related ExternalOrderDTO with fromPlatform:[XIANYU] tid: [{orderRefundDto.biz_order_id}]");
                    return Ok("not found ExternalOrderDTO, 可能是店里其他产品的退款");
                }



                var updateEOResp = await _externalOrderRepository.Update(new LuoliCommon.DTO.ExternalOrder.UpdateRequest()
                {
                    EO = eoResp.data,
                    Event = EEvent.Received_Refund_EO
                });

                if (!updateEOResp.ok)
                {
                    _logger.Warn($"[{requestId}] ReceiveOrderController.ReceiveExternalOrder, update ExternalOrderDTO failed with fromPlatform:[XIANYU] tid: [{orderRefundDto.biz_order_id}]");
                    return BadRequest("update ExternalOrderDTO failed");
                }


                //如果已经消费了
                //卡密更新会失败
                //所以前移统计
                RedisHelper.IncrByAsync(RedisKeys.Prom_ReceivedRefund);

                var coupon = await _couponRepository.Query(eoResp.data.FromPlatform, eoResp.data.Tid).ContinueWith(t => t.Result.data);

                //卡密未消费，作废掉
                if (coupon.Status == ECouponStatus.Shipped)
                {
                    var updateCouponResp = await _couponRepository.Update(new LuoliCommon.DTO.Coupon.UpdateRequest()
                    {
                        Coupon = coupon,
                        Event = EEvent.Received_Refund_EO
                    });

                    if (!updateCouponResp.ok)
                    {
                        _logger.Warn($"[{requestId}] ReceiveOrderController.ReceiveExternalOrder, update CouponDTO failed with fromPlatform:[XIANYU] tid: [{orderRefundDto.biz_order_id}]");
                        return BadRequest("update CouponDTO failed 可能是已经消费过了");
                    }
                }
                //卡密已消费
                else if (coupon.Status == ECouponStatus.Consumed)
                {
                    //茶颜的卡密已消费，需要尝试一下代理退款
                    if (eoResp.data.TargetProxy == ETargetProxy.sexytea)
                    {
                        _logger.Info($"sexytea coupon[{coupon.Coupon}] consumed, have a try to refund");

                        var result = await ApiCaller.PostAsync("http://localhost:8080/api/gateway/admin/sexytea/refund", JsonSerializer.Serialize(
                            new sexyteaRefundReq() { Coupon = coupon.Coupon, OrderNo = coupon.ProxyOrderId }));

                        return Ok("ok, 已经消费，触发后台退款流程" + await result.Content.ReadAsStringAsync());
                    }

                }
                else
                {
                    return BadRequest($"coupon status [{coupon.Status.ToString()}]  不处理");
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

        private static void Notify(TBOrderCreateRequest ocRequest, string coreMsg)
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

        private static void Notify(XYOrderCreateRequest ocRequest, string coreMsg)
        {
            ApiCaller.NotifyAsync(
@$"{Program.Config.ServiceName}.{Program.Config.ServiceId}
msg:{coreMsg}

平台:{ocRequest.FromPlatform}
订单号:{ocRequest.biz_order_id}
销售店铺旺旺:{ocRequest.seller_id}
订单状态:{ocRequest.order_status}", Program.NotifyUsers);
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

            return await _consumeInfoRepository.ConsumeInfoQuery(goodsType,coupon);
        }
    }

}
