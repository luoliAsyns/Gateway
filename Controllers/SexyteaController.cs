using GatewayService.Services.ConsumeInfo;
using GatewayService.Services.Coupon;
using GatewayService.Services.ExternalOrder;
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
using ILogger = LuoliCommon.Logger.ILogger;


namespace GatewayService.Controllers
{

    [Time]
    [Route("api/gateway/prod/sexytea")]
    public class SexyteaController : Controller
    {
        private readonly IExternalOrderRepository _externalOrderRepository;
        private readonly ICouponRepository _couponRepository;
        private readonly IConsumeInfoRepository _consumeInfoRepository;
        private readonly IChannel _channel;
        private readonly ILogger _logger;
        private readonly SexyteaApis _sexyteaApis;

        public SexyteaController(IExternalOrderRepository orderRepository, ICouponRepository couponService, ILogger logger,
           IConsumeInfoRepository consumeInfoRepository, IChannel channel, SexyteaApis sexyteaApis)
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

        [HttpPost]
        [Route("token")]
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
                response.code = EResponseCode.Fail;
                return response;
            }
            _logger.Info("passed parse response from fiddler");
            ApiCaller.NotifyAsync("sexytea token refreshed");

            RedisHelper.SetAsync("sexytea.token", commonToken, 5 * 3600);

            response.msg = "success";
            response.code = EResponseCode.Success;


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

            if (commonToken.status != "NORMAL")
            {
                result.Item2 = $"status [{commonToken.status}] is not NORMAL";
                return result;
            }

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
        [Route("consume")]
        public async Task<ApiResponse<bool>> Consume([FromBody] ConsumeInfoDTO consumeInfo)
        {
            _logger.Info($"trigger SexyteaController.Consume with [{JsonSerializer.Serialize(consumeInfo)}] ");

            ApiResponse<bool> response = new ApiResponse<bool>();
            response.code = EResponseCode.Fail;
            response.data = false;

            try
            {
                await _channel.BasicPublishAsync(exchange: string.Empty,
                 routingKey: RabbitMQKeys.ConsumeInfoInserting,
                 true,
                 _rabbitMQMsgProps,
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(consumeInfo)));

                response.data = true;
                response.msg = "sent consumeInfo to MQ success";
                response.code = EResponseCode.Success;

                _logger.Info($"sent consumeInfo with coupon[{consumeInfo.Coupon}] to MQ [{RabbitMQKeys.ConsumeInfoInserting}] success");

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
        [Route("order")]
        public async Task<ApiResponse<dynamic>> GetOrder([FromQuery] string orderNo)
        {
            _logger.Info($"trigger SexyteaController.GetOrder with orderNo:[{orderNo}] ");
            ApiResponse<dynamic> response = new ApiResponse<dynamic>();

            response.data = null;
            response.code =  EResponseCode.Fail;

            var account = await RedisHelper.GetAsync<Account>("sexytea.token");

            var order = await _sexyteaApis.GetOrderInfo(account, orderNo);

            if (order is null)
            {
                response.code = EResponseCode.NotFound;
                response.msg = "order not found";
                response.data = null;
                return response;
            }
            response.code = EResponseCode.Success;
            response.msg = "success";
            response.data = order;
            return response;
        }
    }
}
