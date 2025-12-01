
using LuoliCommon.DTO.ConsumeInfo;
using LuoliCommon.DTO.ConsumeInfo.Sexytea;
using LuoliCommon.Entities;
using LuoliCommon.Enums;
using LuoliHelper.Entities;
using LuoliUtils;
using MethodTimer;
using Microsoft.AspNetCore.Mvc;
using RabbitMQ.Client;
using System.Text;
using System.Text.Json;
using ThirdApis;
using ThirdApis.Services.ConsumeInfo;
using ThirdApis.Services.Coupon;
using ThirdApis.Services.ExternalOrder;
using ILogger = LuoliCommon.Logger.ILogger;


namespace GatewayService.Controllers
{

    public class SexyteaController : Controller
    {
        private static JsonSerializerOptions _options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true, // 关键配置：忽略大小写
        };
        private readonly IExternalOrderRepository _externalOrderRepository;
        private readonly ICouponRepository _couponRepository;
        private readonly IConsumeInfoRepository _consumeInfoRepository;

        private readonly IChannel _channel;
        private readonly ILogger _logger;
        private readonly SexyteaApis _sexyteaApis;
    

        public SexyteaController(
            IExternalOrderRepository orderRepository, 
            ICouponRepository couponService, 
            IConsumeInfoRepository consumeInfoRepository, 
           ILogger logger, IChannel channel, SexyteaApis sexyteaApis)
        {
            _externalOrderRepository = orderRepository;
            _couponRepository = couponService;
            _consumeInfoRepository = consumeInfoRepository;
            _channel = channel;
            _logger = logger;
            _sexyteaApis = sexyteaApis;
            _rabbitMQMsgProps.ContentType = "text/plain";
            _rabbitMQMsgProps.DeliveryMode = DeliveryModes.Persistent;
        }

        private static BasicProperties _rabbitMQMsgProps = new BasicProperties();



      

        #region  对外公共api
        [HttpGet]
        [Time]
        [Route("api/gateway/prod/sexytea/city")]
        public async Task<ApiResponse<string>> GetCity([FromQuery] int branchId)
        {
            string requestId = HttpContext.TraceIdentifier;

            _logger.Info($"[{requestId}] trigger SexyteaController.GetCity with branchId:[{branchId}] ");

            ApiResponse<Dictionary<int, string>> response = new();

            var city = await RedisHelper.HGetAsync(RedisKeys.SexyteaBranchId2City, branchId.ToString());

            _logger.Info($"[{requestId}] return city:[{city}] ");

            return new ApiResponse<string>()
            {
                code = EResponseCode.Success,
                msg = "success",
                data = city
            };
        }

       


        [HttpPost]
        [Time]
        [Route("api/gateway/prod/sexytea/token")]
        public async Task<ApiResponse<string>> RefreshToken([FromBody] NotifyRequest request)
        {
            // e.g.
            //{"code":200,"data":{"token":"eyJhbGciOiJIUzI1NiJ9.eyJqdGkiOiIyMTQxOTMwNyIsInN1YiI6Ind4bXAiLCJsb25nQWRkciI6Ijk3NDMzOTEwNiIsInVzZXJObyI6IjA3MzExMDcxMTIzMSIsImJyYW5kSWQiOjEwMCwiaWF0IjoxNzU3OTQ0MTAyLCJleHAiOjE3NTc5NjU3MDJ9.iOnpobYM-kDrLJQ4gxyw9KOEdet9Pc2-B1lJZWeGjDQ","user":{"activated":1,"expiration":null,"fAppellation":"小主","fAvatarurl":"https://thirdwx.qlogo.cn/mmopen/vi_32/POgEwh4mIHO4nibH0KlMECNjjGxQUq24ZEaGT4poC6icRiccVGKSyXwibcPq4BWmiaIGuG1icwxaQX6grC9VemZoJ8rg/132","fBirthday":"1996-05-20 00:00:00","fFirstname":"","fGender":"male","fId":21419307,"fLastname":"骆","fMobile":"175****9503","fNickname":"微信用户","fNumber":"073110711231","fRegisteredtime":"2021-08-11 16:57:50","fSource":"MP","openId":"oPL4F5qmjC6pI_K_LMnC71w5yUOM","status":"NORMAL","unionId":"oK2M70mtaWpzV7vbVKNdh--AALHU"}},"msg":"操作成功","ok":true}
            _logger.Info($"trigger SexyteaController.Token with [{request.content}] ");

            string content = request.content;
            ApiResponse<string> response = new ApiResponse<string>();

            var (valid, errorMsg, commonToken) = validate(content);
            if (!valid)
            {
                response.msg = errorMsg;
                response.code= EResponseCode.Fail;
                _logger.Error(errorMsg);
                return response;
            }
            _logger.Info("passed parse response from fiddler");
            ApiCaller.NotifyAsync($"sexytea token refreshed {commonToken.phone}");

            RedisHelper.SetAsync("sexytea.token", commonToken, 6 * 3600);

            response.msg = "success";
            response.code = EResponseCode.Success;


            return response;
        }

        [HttpGet]
        [Time]
        [Route("api/gateway/prod/sexytea/refresh-region-branch-map")]
        public async Task<ApiResponse<bool>> RefreshRegionBranchMap()
        {
            _logger.Info("trigger SexyteaController.RefreshRegionBranchMap");

          
            ApiResponse<bool> response = new ApiResponse<bool>();


            var cities = await  _sexyteaApis.GetRegions();
            _logger.Info($"SexyteaController.RefreshRegionBranchMap found [{cities.Count}] cities");
            int branchesCount = 0;
            foreach(var city in cities)
            {
                var branches = await _sexyteaApis.GetBranchIdsInRegion(city);
                branchesCount+= branches.Count;
                _logger.Info($"SexyteaController.RefreshRegionBranchMap found [{branches.Count}] branches in city[{city}]");
                foreach(var branch in branches)
                    RedisHelper.HSetAsync(RedisKeys.SexyteaBranchId2City, branch.ToString(), city);
            }   


            response.msg = $"success, cities:[{cities.Count}], branches:[{branchesCount}]";
            response.code = EResponseCode.Success;
            response.data = true;

            return response;
        }



        private (bool, string, Token) validate(string content)
        {
            Token commonToken = new Token();
            var result = (false, string.Empty, commonToken);

            try
            {
                dynamic obj = System.Text.Json.JsonSerializer.Deserialize<dynamic>(content);
                dynamic user = obj.GetProperty("data").GetProperty("user");

                commonToken.token = obj.GetProperty("data").GetProperty("token").GetString();
                commonToken.code = obj.GetProperty("code").GetRawText();
                commonToken.status = user.GetProperty("status").GetString();
                commonToken.openId = user.GetProperty("openId").GetString();
                commonToken.unionId = user.GetProperty("unionId").GetString();
                commonToken.phone = user.GetProperty("fMobile").GetString();
            }
            catch (Exception ex)
            {
                _logger.Error("error while RefreshTokenController.validate");
                _logger.Error(ex.Message);
                ApiCaller.NotifyAsync($"error while RefreshTokenController.validate, content:{content}");
                result.Item2 = "error while RefreshTokenController.validate" + ex.Message;
                return result;
            }

            if (commonToken.code != "200")
            {
                result.Item2 = $"code [{commonToken.code}] is not 200";
                return result;
            }

            //if (commonToken.status != "NORMAL")
            //{
            //    result.Item2 = $"status [{commonToken.status}] is not NORMAL";
            //    return result;
            //}

            var tokenSplits = commonToken.token.Split(".");
            if (tokenSplits.Length != 3)
            {
                result.Item2 = $"token [{commonToken.token}] after split, length not 3";
                return result;
            }

            var tokenDecorderResult = LuoliUtils.Decoder.Base64(tokenSplits[1]);
            if (!tokenDecorderResult.Item1)
            {
                result.Item2 = $"token [{tokenSplits[1]}] decode fail:{tokenDecorderResult.Item2}";
                return result;
            }


            try
            {
                dynamic obj = System.Text.Json.JsonSerializer.Deserialize<dynamic>(tokenDecorderResult.Item2);
                long iat = obj.GetProperty("iat").GetInt64();
                long exp = obj.GetProperty("exp").GetInt64();

                commonToken.exp = DateTimeOffset.FromUnixTimeSeconds(exp).DateTime.ToLocalTime();

                return (true, string.Empty, commonToken);
            }
            catch (Exception ex)
            {
                _logger.Error("error while RefreshTokenController.validate; convert token 2nd");
                _logger.Error(ex.Message);
                ApiCaller.NotifyAsync($"error while RefreshTokenController.validate convert token 2nd, tokenDecorderResult.Item2:{tokenDecorderResult.Item2}");
                result.Item2 = "error while RefreshTokenController.validate; convert token 2nd" + ex.Message;
                return result;
            }


        }


        [HttpPost]
        [Time]
        [Route("api/gateway/prod/sexytea/consume")]
        public async Task<ApiResponse<bool>> Consume([FromBody] ConsumeInfoDTO consumeInfo)
        {
            _logger.Info($"trigger SexyteaController.Consume with [{JsonSerializer.Serialize(consumeInfo)}] ");

            ApiResponse<bool> response = new ApiResponse<bool>();
            response.code = EResponseCode.Fail;
            response.data = false;

            try
            {
                var couponExist = await RedisHelper.ExistsAsync(consumeInfo.Coupon);

                if (!couponExist)
                {
                    response.data = false;
                    response.msg = "你的卡密不存在，可能是超时了";
                    return response;
                }

                var tokenExist = await RedisHelper.ExistsAsync(RedisKeys.SexyteaTokenAccount);

                if (!tokenExist)
                {
                    response.data = false;
                    response.msg = "当前后台账户已过期，请联系客服";
                    return response;
                }

                int branchId = JsonSerializer.Deserialize<SexyteaGoods> (consumeInfo.Goods.ToString(), _options).BranchId;
                bool isBannedBranch = await RedisHelper.SIsMemberAsync(RedisKeys.SexyteaBannedBranchId, branchId.ToString());
                if (isBannedBranch)
                {
                    response.data = false;
                    response.msg = "当前门店已被封禁，无法消费，请联系客服";
                    string msg = $"前端已经禁止了的店铺[{branchId}]，依旧发送到了后端，考虑有人搞";
                    _logger.Error(msg);
                    ApiCaller.NotifyAsync(msg);
                    return response;
                }

                //coupon只能消费一次
                //如果通过coupon查不到CI 说明是第一次消费
                var existedCoupon = await _consumeInfoRepository.ConsumeInfoQuery(consumeInfo.GoodsType, consumeInfo.Coupon);
                if (!(existedCoupon.data is null))
                {
                    response.msg = "卡密已经使用过，请勿重复提交";
                    response.data = false;
                    return response;
                }



                await _channel.BasicPublishAsync(exchange: string.Empty,
                 routingKey: Program.Config.KVPairs["StartWith"] + RabbitMQKeys.ConsumeInfoInserting,
                 true,
                 _rabbitMQMsgProps,
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(consumeInfo)));


                response.data = true;
                response.msg = "sent consumeInfo to MQ success";
                response.code = EResponseCode.Success;

                _logger.Info($"sent consumeInfo with coupon[{consumeInfo.Coupon}] to MQ [{Program.Config.KVPairs["StartWith"] + RabbitMQKeys.ConsumeInfoInserting}] success");

                return response;
            }
            catch (Exception ex)
            {
                _logger.Error("while SexyteaController.Consume ");
                _logger.Error(ex.Message);
                response.msg = ex.Message;
                response.code = EResponseCode.Fail;
                response.data = false;
                return response;
            }
        }


        [HttpGet]
        [Time]
        [Route("api/gateway/prod/sexytea/order")]
        public async Task<ApiResponse<dynamic>> GetOrder([FromQuery] string coupon)
        {

            _logger.Info($"trigger SexyteaController.GetOrder with coupon:[{coupon}] ");
            ApiResponse<dynamic> response = new ApiResponse<dynamic>();

            response.data = null;
            response.code =  EResponseCode.Fail;

            var couponDto = await _couponRepository.Query(coupon);
            if(couponDto.data?.ProxyOrderId.Length < 3)
            {
                response.msg = "订单处理中";
                response.data = null;
                return response;
            }


            var account = await RedisHelper.GetAsync<Account>("sexytea.token");

            if(account is null)
            {
                response.code = EResponseCode.Fail;
                response.msg = "Sexytea token expired";
                response.data = null;
                return response;
            }

            var order = await _sexyteaApis.GetOrderInfo(account, couponDto.data.ProxyOrderId);

            if (order is null)
            {
                response.code = EResponseCode.NotFound;
                response.msg = "order not found";
                response.data = null;
                return response;
            }

            try
            {
                JsonDocument doc = JsonDocument.Parse(order.ToString());
                decimal proxyPayment = doc.RootElement.GetProperty("data").GetProperty("balanceAmount").GetDecimal();
                if (couponDto.data.ProxyPayment != proxyPayment)
                {
                    couponDto.data.ProxyPayment = proxyPayment;
                    await _couponRepository.Update(new LuoliCommon.DTO.Coupon.UpdateRequest()
                    {
                        Coupon = couponDto.data,
                        Event = EEvent.ProxyQuery
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.Error("while SexyteaController.GetOrder update proxyPayment ");
                _logger.Error(ex.Message);
            }


            response.code = EResponseCode.Success;
            response.msg = "success";
            response.data = order;
            return response;
        }

        #endregion


        #region  对内admin api  需要token或者是局域网

        public class sexyteaRefundReq
        {
            public string OrderNo { get; set; }
            public string Coupon { get; set; }
        }

        [HttpPost]
        [Time]
        [Route("api/gateway/admin/sexytea/refund")]
        public async Task<ApiResponse<bool>> Refund([FromBody] sexyteaRefundReq req)
        {
            string orderNo = req.OrderNo;

            _logger.Info($"trigger SexyteaController.Refund with orderNo[{orderNo}] ");

            ApiResponse<bool> response = new ApiResponse<bool>();
            response.code = EResponseCode.Fail;
            response.data = false;

            try
            {
                var account = await RedisHelper.GetAsync<Account>("sexytea.token");

                if (account is null)
                {
                    response.code = EResponseCode.Fail;
                    response.msg = "Sexytea token expired";
                    response.data = false;
                    return response;
                }

                var refundResult = await _sexyteaApis.OrderRefund(account, orderNo);
                response.data = refundResult.Item1;
                response.msg = refundResult.Item2;
                response.code = EResponseCode.Success;

                _logger.Info($"SexyteaController.Refund, [{refundResult.Item1},[{refundResult.Item2}]");
                if(response.data)
                {
                    var couponRep = await _couponRepository.Query(req.Coupon);
                    await _couponRepository.Update(new LuoliCommon.DTO.Coupon.UpdateRequest()
                    {
                        Coupon = couponRep.data,
                        Event = EEvent.ProxyRefund
                    });
                }

                return response;
            }
            catch (Exception ex)
            {
                _logger.Error("while SexyteaController.Refund ");
                _logger.Error(ex.Message);
                response.msg = ex.Message;
                response.code = EResponseCode.Fail;
                response.data = false;
                return response;
            }
        }


        [HttpGet]
        [Time]
        [Route("api/gateway/admin/sexytea/user-info")]
        public async Task<ApiResponse<dynamic>> GetUserInfo()
        {

            _logger.Info($"trigger SexyteaController.GetUserInfo");

            ApiResponse<dynamic> response = new ApiResponse<dynamic>();
            response.code = EResponseCode.Fail;
            response.data = null;

            try
            {
                var account = await RedisHelper.GetAsync<Account>("sexytea.token");

                if (account is null)
                {
                    response.code = EResponseCode.Fail;
                    response.msg = "Sexytea token expired";
                    response.data = null;
                    return response;
                }

                var refundResult = await _sexyteaApis.UserInfo(account);

                response.data = refundResult;
                response.msg = string.Empty;
                response.code = EResponseCode.Success;

                _logger.Info($"SexyteaController.GetUserInfo success");

                return response;
            }
            catch (Exception ex)
            {
                _logger.Error("while SexyteaController.GetUserInfo ");
                _logger.Error(ex.Message);
                response.msg = ex.Message;
                response.code = EResponseCode.Fail;
                response.data = null;
                return response;
            }
        }


        [HttpGet]
        [Time]
        [Route("api/gateway/admin/sexytea/token")]
        public async Task<ApiResponse<Account>> GetToken()
        {

            _logger.Info($"trigger SexyteaController.GetToken");

            ApiResponse<Account> response = new ApiResponse<Account>();
            response.code = EResponseCode.Fail;
            response.data = null;

            try
            {
                var account = await RedisHelper.GetAsync<Account>("sexytea.token");

                if (account is null)
                {
                    response.code = EResponseCode.Fail;
                    response.msg = "Sexytea token expired";
                    response.data = null;
                    return response;
                }


                response.data = account;
                response.msg = string.Empty;
                response.code = EResponseCode.Success;


                return response;
            }
            catch (Exception ex)
            {
                _logger.Error("while SexyteaController.GetToken ");
                _logger.Error(ex.Message);
                response.msg = ex.Message;
                response.code = EResponseCode.Fail;
                response.data = null;
                return response;
            }
        }




        #endregion

    }
}
