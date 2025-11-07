using GatewayService.MiddleWares;
using GatewayService.User;
using LuoliCommon.DTO;
using LuoliCommon.DTO.ExternalOrder;
using LuoliCommon.DTO.User;
using LuoliCommon.Entities;
using LuoliUtils;
using Microsoft.AspNetCore.Identity.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Polly;
using System.Drawing;
using ThirdApis.Services.ConsumeInfo;
using ThirdApis.Services.Coupon;
using ThirdApis.Services.ExternalOrder;

namespace GatewayService.Controllers
{

    [Route("api/gateway/admin")]
    public class AdminController : Controller
    {

        private readonly LuoliCommon.Logger.ILogger _logger;

        private readonly IUserRepository _userRepository;
        private readonly IExternalOrderRepository _externalOrderRepository;
        private readonly ICouponRepository _couponRepository;
        private readonly IConsumeInfoRepository _consumeInfoRepository;

        private readonly IJwtService _jwtService;
        public AdminController(LuoliCommon.Logger.ILogger logger,  IJwtService jwtService,
            IUserRepository userRepository,
            IExternalOrderRepository externalOrderRepository,
            ICouponRepository couponService,
            IConsumeInfoRepository consumeInfoRepository)
        {
            _logger = logger;
            _userRepository = userRepository;
            _externalOrderRepository = externalOrderRepository;
            _jwtService = jwtService;
        }


        [HttpPost]
        [Route("login")]
        public async Task<ApiResponse<dynamic>> Login([FromBody] LuoliCommon.DTO.User.LoginRequest loginRequest)
        {
            _logger.Info($"trigger AdminController.Login: user[{loginRequest.UserName}]");

            ApiResponse<dynamic> resp = new ApiResponse<dynamic>();
            resp.code = LuoliCommon.Enums.EResponseCode.Fail;
            resp.data = null;


            var loginResp = await _userRepository.Login(loginRequest.UserName, loginRequest.Password);

            if (loginResp.data)
            {
                // 2. 生成JWT令牌
                var token = _jwtService.GenerateToken(loginRequest.UserName);

                // 3. 返回令牌和用户基本信息
                RedisHelper.SetAsync($"admin.{loginRequest.UserName}", token, 60 * 60);

                resp.data = new
                {
                    Token = token,
                    ExpiresIn = 3600,
                    User = loginRequest.UserName
                };
                resp.code = LuoliCommon.Enums.EResponseCode.Success;

                _logger.Info($"AdminController.Login success & Set token in redis: user[{loginRequest.UserName}]");

                return resp;
            }
            _logger.Error($"AdminController.Login failed: user[{loginRequest.UserName}]");

            resp.msg = "登录失败";

            return resp;
        }

        [HttpPost]
        [Route("logout")]
        public async Task<ApiResponse<bool>> Logout()
        {
            string user = HttpContext.Items["User"].ToString();

            _logger.Info($"trigger AdminController.Logout: user[{user}]");

            ApiResponse<bool> resp = new ApiResponse<bool>();
            resp.code = LuoliCommon.Enums.EResponseCode.Fail;
            resp.data = false;


            RedisHelper.DelAsync($"admin.{user}");

            resp.data = true;
            resp.code = LuoliCommon.Enums.EResponseCode.Success;

            _logger.Info($"AdminController.Logout success, remove token in redis: user[{user}]");

            return resp;
        }

        [HttpPost]
        [Route("change-password")]
        public async Task<ApiResponse<bool>> ChangePassword([FromBody] LuoliCommon.DTO.User.ChangePasswordRequest chRequest)
        {

            string user = HttpContext.Items["User"].ToString();

            _logger.Info($"trigger AdminController.ChangePassword: user from context[{user}], input user:[{chRequest.UserName}]");

            ApiResponse<bool> resp = new ApiResponse<bool>();
            resp.code = LuoliCommon.Enums.EResponseCode.Fail;
            resp.data = false;

            if(user == "luoli" || user == chRequest.UserName)
            {
                RedisHelper.DelAsync($"admin.{user}");

                resp = await _userRepository.ChangePassword(chRequest.UserName, chRequest.Password);

                RedisHelper.DelAsync($"admin.{user}");
                resp.msg = "修改密码成功";
                _logger.Info($"AdminController.ChangePassword success, remove token in redis: user[{user}]");
                return resp;
            }

            resp.msg = "只能修改自己的密码";
            _logger.Error($"AdminController.ChangePassword failed: user from context[{user}], input user:[{chRequest.UserName}]");

            return resp;
        }


        [HttpGet]
        [Route("prom")]
        public async Task<ApiResponse<dynamic>> GetPrometheusData()
        {
            _logger.Info($"trigger AdminController.GetPrometheusData");

            ApiResponse<dynamic> resp = new ApiResponse<dynamic>();
            resp.code = LuoliCommon.Enums.EResponseCode.Success;
            resp.data = new
            {
                Prom_ReceivedOrders = await RedisHelper.GetAsync<int>(RedisKeys.Prom_ReceivedOrders),
                Prom_ReceivedRefund = await RedisHelper.GetAsync<int>(RedisKeys.Prom_ReceivedRefund),
                Prom_CouponsGenerated = await RedisHelper.GetAsync<int>(RedisKeys.Prom_CouponsGenerated),
                Prom_Shipped = await RedisHelper.GetAsync<int>(RedisKeys.Prom_Shipped),
                Prom_ShipFailed = await RedisHelper.GetAsync<int>(RedisKeys.Prom_ShipFailed),
                Prom_ReceivedConsumeInfo = await RedisHelper.GetAsync<int>(RedisKeys.Prom_ReceivedConsumeInfo),
                Prom_InsertedConsumeInfo = await RedisHelper.GetAsync<int>(RedisKeys.Prom_InsertedConsumeInfo),
                Prom_PlacedOrders = await RedisHelper.GetAsync<int>(RedisKeys.Prom_PlacedOrders),
                Prom_PlacedOrdersFailed = await RedisHelper.GetAsync<int>(RedisKeys.Prom_PlacedOrdersFailed),
            };
            
            return resp;
        }

        [HttpGet]
        [Route("order-page-query")]
        public async Task<ApiResponse<PageResult<TableItemVM>>> GetPageEOs(
            [FromQuery] int page,
            [FromQuery] int size,
            [FromQuery] DateTime? startTime = null,
            [FromQuery] DateTime? endTime = null)
        {
            _logger.Info($"trigger AdminController.GetPageEOs, page[{page}],size[{size}]");

            var eoResp = await _externalOrderRepository.PageQueryAsync(page, size, startTime, endTime);


            ApiResponse<PageResult<TableItemVM>> result = new ApiResponse<PageResult<TableItemVM>>();
            result.data = new PageResult<TableItemVM>();
            result.data.Page = eoResp.data.Page;
            result.data.Total = eoResp.data.Total;
            result.data.Size = eoResp.data.Size;
            result.data.Items = new List<TableItemVM>();

            foreach (var eo in eoResp.data.Items)
            {
                var couponResp = await _couponRepository.Query(eo.FromPlatform, eo.Tid);
                var ciResp = await _consumeInfoRepository.ConsumeInfoQuery(eo.TargetProxy.ToString() + "_consume_info", couponResp.data.Coupon);
                var tableItemVM = new TableItemVM(eo, couponResp.data, ciResp.data);
                result.data.Items.Add(tableItemVM);
            }

            // 先创建所有并行任务
            var tasks = eoResp.data.Items.Select(async eo =>
            {
                var couponResp = await _couponRepository.Query(eo.FromPlatform, eo.Tid);
                var ciResp = await _consumeInfoRepository.ConsumeInfoQuery(
                    $"{eo.TargetProxy.ToString()}_consume_info",
                    couponResp.data.Coupon
                );

                // 返回当前项的ViewModel
                return new TableItemVM(eo, couponResp.data, ciResp.data);
            }).ToList();

            // 等待所有任务完成并合并结果
            var tableItems = await Task.WhenAll(tasks);
            var sortedItems = tableItems.OrderBy(item => item.EO.CreateTime).ToList();

            result.data.Items = sortedItems;
            return result;
        }

        [HttpPost]
        [Route("coupon-invalidate")]
        public async Task<ApiResponse<bool>> CouponInvalidate([FromBody] string coupon)
        {
            _logger.Info($"trigger AdminController.CouponInvalidate with coupon[{coupon}]");

            return await _couponRepository.Invalidate(coupon); ; 
        }


    }
}
