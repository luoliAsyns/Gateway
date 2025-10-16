using GatewayService.Services.Coupon;
using GatewayService.Services.ExternalOrder;
using LuoliCommon.Entities;
using LuoliCommon.Enums;
using LuoliUtils;
using MethodTimer;
using Microsoft.AspNetCore.Mvc;
using ILogger = LuoliCommon.Logger.ILogger;


namespace GatewayService.Controllers
{

    [Time]
    [Route("api/gateway/sexytea")]
    public class SexyteaController : Controller
    {
        private readonly IExternalOrderRepository _externalOrderRepository;
        private readonly ICouponRepository _couponRepository;
        private readonly ILogger _logger;

        public SexyteaController(IExternalOrderRepository orderRepository, ICouponRepository couponService, ILogger logger)
        {
            _externalOrderRepository = orderRepository;
            _couponRepository = couponService;
            _logger = logger;
        }
      

        [HttpGet]
        [Route("city")]
        public async Task<ApiResponse<string>> GetCity([FromQuery] int branchId)
        {
            string requestId = HttpContext.TraceIdentifier;

            _logger.Info($"[{requestId}] trigger SexyteaController.GetCity with branchId:[{branchId}] ");

            ApiResponse<Dictionary<int, string>> response = new();

            var city = RedisHelper.HGet(RedisKeys.BranchId2City, branchId.ToString());

            _logger.Info($"[{requestId}] return city:[{city}] ");

            return new ApiResponse<string>()
            {
                code = EResponseCode.Success,
                msg = "success",
                data = city
            };
        }


        //[HttpGet]
        //[Route("consume-order-info")]
        //public async Task<ApiResponse<OrderFeedbackDto>> GetOrderInfo([FromQuery(Name = "coupon")] string coupon)
        //{
        //    string requestId = HttpContext.TraceIdentifier;
        //    decimal balance = 0;
        //    _logger.Info($"[{requestId}] trigger SexyteaController.GetOrderInfo with coupon:[{coupon}] ");

        //    ApiResponse<OrderFeedbackDto> response = new();
        //    response.data = new OrderFeedbackDto();
        //    response.data.consume_coupon = coupon;

        //    MOrder order = await _externalOrderRepository.GetOrderByCoupon(coupon);
        //    try
        //    {
        //        if (order is null)
        //        {
        //            response.code = EResponseCode.Fail;
        //            response.msg = "coupon 相关的order不存在";
        //            return response;
        //        }

        //        if (!_couponRepository.Exist(coupon))
        //        {
        //            response.code = EResponseCode.Fail;
        //            response.msg = "coupon 用过啦";
        //            response.data.balance = 0;

        //            return response;
        //        }

        //        var couponStatus = (ECouponStatus)order.consume_coupon_status;

        //        response.msg = "success";
        //        response.code = EResponseCode.Success;
        //        response.data.pay_amount = order.pay_amount;
        //        response.data.consume_status = SEnum2Dict.GetDescription(couponStatus);
        //        response.data.consume_order_no = "";

        //        //只有是 “已生成”的状态下，才显示正常额度，否则就是0
        //        response.data.balance = couponStatus == ECouponStatus.Generated ? order.pay_amount / 0.80m : 0;

        //        response.data.create_time = order.create_time;
        //        response.data.receiver_phone = order.receiver_phone;
        //        response.data.receiver_address = order.receiver_address;
        //        response.data.receiver_name = order.receiver_name;
        //        response.data.consume_content = order.consume_content is null ? null : System.Text.Json.JsonSerializer.Deserialize<List<MOrderItem>>(order.consume_content);

        //        return response;
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.Error("while SexyteaController.GetBalance ");
        //        _logger.Error(ex.Message);
        //        response.msg = ex.Message;
        //        response.code = EResponseCode.Fail;
        //        response.data = null;
        //        return response;
        //    }

        //}
    }
}
