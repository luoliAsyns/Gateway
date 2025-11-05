using GatewayService.MiddleWares;
using GatewayService.User;
using LuoliCommon.DTO.User;
using LuoliCommon.Entities;
using Microsoft.AspNetCore.Identity.Data;
using Microsoft.AspNetCore.Mvc;
using Polly;

namespace GatewayService.Controllers
{

    [Route("api/gateway/admin")]
    public class AdminController : Controller
    {

        private readonly LuoliCommon.Logger.ILogger _logger;
        private readonly IUserRepository _userRepository;
        private readonly IJwtService _jwtService;
        public AdminController(LuoliCommon.Logger.ILogger logger, IUserRepository userRepository, IJwtService jwtService)
        {
            _logger = logger;
            _userRepository = userRepository;
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


        [HttpPost]
        [Route("register")]
        public async Task<ApiResponse<string>> Register([FromBody] LuoliCommon.DTO.User.RegisterRequest registerRequest)
        {

            string user = HttpContext.Items["User"].ToString();

            _logger.Info($"trigger AdminController.ChangePassword: user from context[{user}], input user:[{registerRequest.UserName}]");

            ApiResponse<string> resp = new ApiResponse<string>();
            resp.code = LuoliCommon.Enums.EResponseCode.Fail;
            resp.data = string.Empty;

            if (user == "luoli")
            {
                RedisHelper.DelAsync($"admin.{user}");

                resp = await _userRepository.Register(registerRequest.UserName, registerRequest.Phone, registerRequest.Gender);

                RedisHelper.DelAsync($"admin.{user}");
                resp.msg = "注册成功,初始密码在data里";
                _logger.Info($"AdminController.Register success: new user[{registerRequest.UserName}]");
                return resp;
            }


            resp.msg = "只有luoli可以创建账号";
            _logger.Error($"AdminController.Register failed: user from context[{user}], input user:[{registerRequest.UserName}]");

            return resp;

        }
    }
}
