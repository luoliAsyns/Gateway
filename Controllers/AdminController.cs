using GatewayService.MiddleWares;
using GatewayService.User;
using LuoliCommon.DTO;
using LuoliCommon.DTO.Admin;
using LuoliCommon.DTO.ConsumeInfo.Sexytea;
using LuoliCommon.DTO.ExternalOrder;
using LuoliCommon.DTO.User;
using LuoliCommon.Entities;
using LuoliCommon.Enums;
using LuoliUtils;
using MethodTimer;
using Microsoft.AspNetCore.Identity.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Polly;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Text.Json;
using ThirdApis;
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
            _couponRepository = couponService;
            _consumeInfoRepository = consumeInfoRepository;

            _jwtService = jwtService;
        }


        [HttpGet]
        [Time]
        [Route("~/api/gateway/prod/place-order-button-enable")]
        public async Task<ApiResponse<dynamic>> GetPlaceOrderButtonEnable([FromQuery] string targetProxy = null)
        {
            var resp = new ApiResponse<dynamic>();
            resp.code = EResponseCode.Success;
            resp.msg = string.Empty;
            if (targetProxy is null)
                resp.data = await RedisHelper.HGetAllAsync<bool>(RedisKeys.PlaceOrderButtonEnable);
            else
                resp.data = await RedisHelper.HGetAsync<bool>(RedisKeys.PlaceOrderButtonEnable, targetProxy);

            return resp;
        }

        [HttpGet]
        [Time]
        [Route("~/api/gateway/prod/banned-branches")]
        public async Task<ApiResponse<dynamic>> GetBannedBranchs([FromQuery] string targetProxy = null)
        {
            var resp = new ApiResponse<dynamic>();
            resp.code = EResponseCode.Success;
            resp.msg = string.Empty;
            switch(targetProxy)
            {
                case "sexytea":
                    resp.data = await RedisHelper.SMembersAsync(RedisKeys.SexyteaBannedBranchId);
                    break;
                default:
                    resp.data = null;
                    break;
            }
          
            return resp;
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
                RedisHelper.SetAsync($"admin.{loginRequest.UserName}", token, 18* 60 * 60);

                resp.data = new
                {
                    Token = token,
                    ExpiresIn = 3600 * 18,
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


            await RedisHelper.DelAsync($"admin.{user}");

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


        [HttpPost]
        [Route("register")]
        public async Task<ApiResponse<string>> Register([FromBody] LuoliCommon.DTO.User.ChangePasswordRequest chRequest)
        {

            string user = HttpContext.Items["User"].ToString();

            _logger.Info($"trigger AdminController.Register: user from context[{user}], input user:[{chRequest.UserName}]");

            ApiResponse<string> resp = new ApiResponse<string>();
            resp.code = LuoliCommon.Enums.EResponseCode.Fail;
            resp.data = string.Empty;

            if (user != "luoli" )
            {
                resp.msg = "只有luoli可以注册新用户";
                _logger.Error($"AdminController.Register failed: user from context[{user}], input user:[{chRequest.UserName}]");
                return resp;
            }
            await RedisHelper.DelAsync($"admin.{chRequest.UserName}");

            resp = await _userRepository.Register(chRequest.UserName, "", true);

            _logger.Info($"AdminController.Register success, remove token in redis: user[{chRequest.UserName}]");
            return resp;
        }


        [HttpGet]
        [Route("prom")]
        public async Task<ApiResponse<dynamic>> GetPrometheusData()
        {
            //_logger.Info($"trigger AdminController.GetPrometheusData");

            ApiResponse<dynamic> resp = new ApiResponse<dynamic>();
            resp.code = LuoliCommon.Enums.EResponseCode.Success;
            resp.data = new[]
                 {
                    new
                    {
                        name = "拉取订单",
                        value = await RedisHelper.GetAsync<int>(RedisKeys.Prom_ReceivedOrders),
                        color = "green",
                        metric="order_pulled_total",
                    },
                    new
                    {
                        name = "收到退款请求",
                        value = await RedisHelper.GetAsync<int>(RedisKeys.Prom_ReceivedRefund),
                        color = "pink-darken-2",
                         metric="refund_requests_total",
                    },
                    new
                    {
                        name = "生成卡密",
                        value = await RedisHelper.GetAsync<int>(RedisKeys.Prom_CouponsGenerated) ,
                        color = "green",
                         metric="coupons_generated_total",
                    },
                    new
                    {
                        name = "发货成功",
                        value = await RedisHelper.GetAsync<int>(RedisKeys.Prom_Shipped) ,
                        color = "green",
                         metric="shipped_success_total",
                    },
                    new
                    {
                        name = "发货失败",
                        value = await RedisHelper.GetAsync<int>(RedisKeys.Prom_ShipFailed) ,
                        color = "pink-darken-2",
                         metric="shipped_failed_total",
                    },
                    new
                    {
                        name = "收到消费信息",
                        value = await RedisHelper.GetAsync<int>(RedisKeys.Prom_ReceivedConsumeInfo) ,
                        color = "green",
                         metric="consume_info_received_total",
                    },
                    new
                    {
                        name = "插入消费信息",
                        value = await RedisHelper.GetAsync<int>(RedisKeys.Prom_InsertedConsumeInfo) ,
                        color = "green",
                         metric="consume_info_inserted_total",
                    },
                    new
                    {
                        name = "代理下单成功",
                        value = await RedisHelper.GetAsync<int>(RedisKeys.Prom_PlacedOrders) ,
                        color = "green",
                         metric="agent_orders_success_total",
                    },
                    new
                    {
                        name = "代理下单失败",
                        value = await RedisHelper.GetAsync<int>(RedisKeys.Prom_PlacedOrdersFailed) ,
                        color = "pink-darken-2",
                        metric="agent_orders_failed_total",
                    }
                };

            return resp;
        }



        [HttpPost]
        [Time]
        [Route("place-order-button-enable")]
        public async Task<ApiResponse<bool>> SetPlaceOrderButtonEnable([FromQuery] string targetProxy, [FromQuery] bool enable)
        {
            var resp = new ApiResponse<bool>();
            resp.code = EResponseCode.Success;

            var current = await RedisHelper.HSetAsync(RedisKeys.PlaceOrderButtonEnable, targetProxy, enable);

            if (enable)
            {
                resp.msg = $"当前{targetProxy}可以下单";
                resp.data = true;
            }
            else
            {
                resp.msg = $"当前{targetProxy}已被禁止下单";
                resp.data = false;

            }

            return resp;
        }


        [HttpPost]
        [Time]
        [Route("account-enable")]
        public async Task<ApiResponse<bool>> SetAccountEnable([FromQuery] string targetProxy, [FromQuery] string phone, [FromQuery] bool enable)
        {
            var resp = new ApiResponse<bool>();
            resp.code = EResponseCode.Success;

            if (targetProxy == "sexytea")
            {
                var acc = await RedisHelper.HGetAsync<Account>(RedisKeys.SexyteaTokenAccount, phone);
                acc.Enable = enable;
                await RedisHelper.HSetAsync(RedisKeys.SexyteaTokenAccount, phone, acc);
            }
            else
            {
                resp.msg = $"{targetProxy} 暂不支持这个部分";
                resp.data = false;
                return resp;
            }

            resp.msg = $"当前[{targetProxy}].[{phone}]{(enable ? "" : "不")}可以下单";
            resp.data = enable;

            return resp;
        }




        [HttpPost]
        [Time]
        [Route("banned-branches")]
        public async Task<ApiResponse<bool>> SetBannedBranchs([FromBody] BannedBranchesRequest req)
        {
            var resp = new ApiResponse<bool>();
            resp.code = EResponseCode.Success;

            switch (req.targetProxy)
            {
                case "sexytea":
                    await RedisHelper.DelAsync(RedisKeys.SexyteaBannedBranchId);
                    foreach(var branch in req.branches)
                        RedisHelper.SAddAsync(RedisKeys.SexyteaBannedBranchId, branch);

                    resp.data = true;
                    break;
                default:
                    resp.data = false;
                    resp.msg = "未知代理名，没找到相关的禁止下单店铺";
                    _logger.Warn(resp.msg);
                    break;
            }


            return resp;
        }

        // 定义 Redis 指标与 Prometheus 指标的映射关系
        private readonly Dictionary<string, (string PromName, string Help, string Type)> _metricMappings = new()
        {
            { RedisKeys.Prom_ReceivedOrders, ("order_pulled_total", "拉取订单", "counter") },
            { RedisKeys.Prom_ReceivedRefund, ("refund_requests_total", "收到退款请求", "counter") },
            { RedisKeys.Prom_CouponsGenerated, ("coupons_generated_total", "生成卡密", "counter") },
            { RedisKeys.Prom_Shipped, ("shipped_success_total", "发货成功", "counter") },
            { RedisKeys.Prom_ShipFailed, ("shipped_failed_total", "发货失败", "counter") },
            { RedisKeys.Prom_ReceivedConsumeInfo, ("consume_info_received_total", "收到消费信息", "counter") },
            { RedisKeys.Prom_InsertedConsumeInfo, ("consume_info_inserted_total", "插入消费信息", "counter") },
            { RedisKeys.Prom_PlacedOrders, ("agent_orders_success_total", "代理下单成功", "counter") },
            { RedisKeys.Prom_PlacedOrdersFailed, ("agent_orders_failed_total", "代理下单失败", "counter") }
        };

        [HttpGet]
        [Route("metrics")]
        public async Task<IActionResult> GetMetrics()
        {
            var sb = new StringBuilder();

            // 遍历所有指标映射，读取 Redis 数据并生成 Prometheus 格式
            foreach (var (redisKey, (promName, help, type)) in _metricMappings)
            {
                // 从 Redis 读取值（你的场景是 int 类型）
                var value = await RedisHelper.GetAsync<int>(redisKey);

                // 添加 HELP 注释（可选，建议添加）
                sb.AppendLine($"# HELP {promName} {help}");

                // 添加 TYPE 声明（必须）
                sb.AppendLine($"# TYPE {promName} {type}");

                // 添加指标值（无标签时直接写值）
                sb.AppendLine($"{promName} {value}");

                // 如需换行分隔不同指标，可加空行（可选）
                sb.AppendLine();
            }

            // 返回 Prometheus 格式文本
            return Content(sb.ToString(), "text/plain; charset=utf-8");
        }


        [HttpGet]
        [Route("order-page-query")]
        public async Task<ApiResponse<PageResult<TableItemVM>>> GetPageOrders(
            [FromQuery] int page,
            [FromQuery] int size,
            [FromQuery] DateTime? startTime = null,
            [FromQuery] DateTime? endTime = null,
            [FromQuery] byte? couponStatus = null)
        {
            _logger.Info($"trigger AdminController.GetPageOrders, page[{page}],size[{size}]");

            startTime = startTime?.AddHours(-8);
            endTime = endTime?.AddHours(-8);

            var couponResp = await _couponRepository.PageQuery(page, size, couponStatus, startTime, endTime );


            ApiResponse<PageResult<TableItemVM>> result = new ApiResponse<PageResult<TableItemVM>>();
            result.data = new PageResult<TableItemVM>();
            result.data.Page = couponResp.data.Page;
            result.data.Total = couponResp.data.Total;
            result.data.Size = couponResp.data.Size;
            result.data.Items = new List<TableItemVM>();

           

            // 先创建所有并行任务
            var tasks = couponResp.data.Items.Select(async coupon =>
            {
                var eoResp = await _externalOrderRepository.Get(coupon.ExternalOrderFromPlatform , coupon.ExternalOrderTid);
                var ciResp = await _consumeInfoRepository.ConsumeInfoQuery(
                    $"{eoResp.data.TargetProxy.ToString()}_consume_info",
                    coupon.Coupon
                );

                // 返回当前项的ViewModel
                return new TableItemVM(eoResp.data, coupon, ciResp.data);
            }).ToList();

            // 等待所有任务完成并合并结果
            var tableItems = await Task.WhenAll(tasks);
            var sortedItems = tableItems.OrderByDescending(item => item.EO.CreateTime).ToList();

            result.data.Items = sortedItems;
            return result;
        }


        [HttpGet]
        [Route("order-tid-query")]
        public async Task<ApiResponse<TableItemVM>> GetEO(
            [FromQuery] string tid,
            [FromQuery] string fromPlatform)
        {

            _logger.Info($"trigger AdminController.GetEO, tid[{tid}],fromPlatform [{fromPlatform}]");

            var eoResp = await _externalOrderRepository.Get(fromPlatform, tid);


            ApiResponse<TableItemVM> result = new();
            var eo = eoResp.data;
            var couponResp = await _couponRepository.Query(eo.FromPlatform, eo.Tid);
            var ciResp = await _consumeInfoRepository.ConsumeInfoQuery(
                $"{eo.TargetProxy.ToString()}_consume_info",
                couponResp.data.Coupon
            );

            result.data = new TableItemVM(eo, couponResp.data, ciResp.data);
            result.code = LuoliCommon.Enums.EResponseCode.Success;
            return result;
        }

       

        [HttpPost]
        [Route("coupon-invalidate")]
        public async Task<ApiResponse<bool>> CouponInvalidate([FromBody] InvalidateCouponRequest obj)
        {
            string coupon = obj.Coupon;
            _logger.Info($"trigger AdminController.CouponInvalidate with coupon[{coupon}]");

            return await _couponRepository.Invalidate(coupon);
        }


        [HttpPost]
        [Route("coupon-recover")]
        public async Task<ApiResponse<bool>> CouponRecover([FromBody] InvalidateCouponRequest obj)
        {
            string coupon = obj.Coupon;
            _logger.Info($"trigger AdminController.CouponRecover with coupon[{coupon}]");

            var couponDto = (await _couponRepository.Query(coupon)).data;
            var eoDto = (await _externalOrderRepository.Get(couponDto.ExternalOrderFromPlatform, couponDto.ExternalOrderTid)).data;
            await _externalOrderRepository.Update( new UpdateRequest() { EO= eoDto , Event= EEvent.Receive_Manual_Recover_Coupon});

            return await _couponRepository.Update(new LuoliCommon.DTO.Coupon.UpdateRequest() {Coupon= couponDto, Event = EEvent.Receive_Manual_Recover_Coupon });
        }



        [HttpGet]
        [Route("sku-map")]
        public async Task<ApiResponse<dynamic>> GetSkuIdMap()
        {

            _logger.Info($"trigger SexyteaController.GetSkuIdMap");

            ApiResponse<dynamic> response = new ApiResponse<dynamic>();
            response.code = EResponseCode.Fail;
            response.data =  null;

            try
            {
                Dictionary<string, SkuIdMapItem> map = await RedisHelper.HGetAllAsync<SkuIdMapItem>("skuid2proxy");
                response.data = new
                {
                    map = map,
                    allOptions = map.Values.Select(item=>item.TargetProxy).Distinct()
                };
                response.msg = string.Empty;
                response.code = EResponseCode.Success;

                _logger.Info($"SexyteaController.GetSkuIdMap, length[{map?.Count}]]");

                return response;
            }
            catch (Exception ex)
            {
                _logger.Error("while SexyteaController.GetSkuIdMap ");
                _logger.Error(ex.Message);
                response.msg = ex.Message;
                response.code = EResponseCode.Fail;
                response.data = null;
                return response;
            }
        }


        [HttpPost]
        [Route("sku-map-item-update")]
        public async Task<ApiResponse<bool>> UpdateSkuIdMapItem([FromBody] SkuIdMapChangeRequest req)
        {

            _logger.Info($"trigger SexyteaController.UpdateSkuIdMapItem skuid[{req.skuId}] newValue[{JsonSerializer.Serialize(req.item)}]");


            ApiResponse<bool> response = new ApiResponse<bool>();
            response.code = EResponseCode.Fail;
            response.data = false;

            try
            {
                var result = await RedisHelper.HSetAsync("skuid2proxy", req.skuId , req.item);
                response.data = result;
                response.msg = string.Empty;
                response.code = EResponseCode.Success;

                _logger.Info($"SexyteaController.UpdateSkuIdMapItem, result[{result}]");

                return response;
            }
            catch (Exception ex)
            {
                _logger.Error("while SexyteaController.UpdateSkuIdMapItem ");
                _logger.Error(ex.Message);
                response.msg = ex.Message;
                response.code = EResponseCode.Fail;
                response.data = false;
                return response;
            }
        }


        [HttpPost]
        [Route("sku-map-item-delete")]
        public async Task<ApiResponse<bool>> DeleteSkuIdMapItem([FromBody] SkuIdMapChangeRequest req)
        {

            _logger.Info($"trigger SexyteaController.DeleteSkuIdMapItem skuid[{req.skuId}] ");


            ApiResponse<bool> response = new ApiResponse<bool>();
            response.code = EResponseCode.Fail;
            response.data = false;

            try
            {
                var result = await RedisHelper.HDelAsync("skuid2proxy", req.skuId);
                response.data = true;
                response.msg = string.Empty;
                response.code = EResponseCode.Success;

                _logger.Info($"SexyteaController.DeleteSkuIdMapItem success");

                return response;
            }
            catch (Exception ex)
            {
                _logger.Error("while SexyteaController.DeleteSkuIdMapItem ");
                _logger.Error(ex.Message);
                response.msg = ex.Message;
                response.code = EResponseCode.Fail;
                response.data = false;
                return response;
            }
        }

        


    }
}
