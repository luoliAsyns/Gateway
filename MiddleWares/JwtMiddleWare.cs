using JWT.Algorithms;
using JWT.Builder;
using JWT.Exceptions;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace GatewayService.MiddleWares
{
    public class JwtMiddleware
    {
        private readonly RequestDelegate _next;

        public JwtMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context, IJwtService jwtService)
        {
            // 1.1 跳过登录接口的鉴权
            if (
                context.Request.Path.StartsWithSegments("/api/gateway/prod") ||
                context.Request.Path.StartsWithSegments("/api/gateway/admin/login"))
            {
                await _next(context);
                return;
            }

#if DEBUG

#else
            // 1.2 检查是否是局域网或本机请求
            IPAddress ip = GetClientIpAddress(context);

            if (IsLocalOrLanIp(ip))
            {
                Console.WriteLine($"ip:[{string.Join(".", ip.GetAddressBytes())}], regarded as local");
                await _next(context);
                return;
            }
            Console.WriteLine($"ip:[{string.Join(".", ip.GetAddressBytes())}], regarded as outside");

#endif

            // 2. 从请求头获取令牌
            var token = GetTokenFromHeader(context);
            if (string.IsNullOrEmpty(token))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { message = "未提供令牌" });
                return;
            }

            try
            {
                // 3. 验证令牌并获取用户信息
                var payload =await jwtService.ValidateToken(token);

                // 4. 将用户信息存入上下文，供后续控制器使用
                context.Items["User"] = payload["name"].ToString();

                // 5. 继续处理请求
                await _next(context);
            }
            catch (Exception ex)
            {
                // 1. 设置 401 状态码
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                // 2. 设置响应头（尤其跨域场景，必须确保 CORS 头正确）
                context.Response.Headers.Add("Access-Control-Allow-Origin", context.Request.Headers["Origin"].FirstOrDefault() ?? "*");
                context.Response.Headers.Add("Content-Type", "application/json");
                // 3. 写入响应内容，并确保响应流完成
                await context.Response.WriteAsJsonAsync(new { message = ex.Message });
                // 4. 关键：结束响应（避免响应挂起）
                await context.Response.CompleteAsync();
                return; // 终止后续中间件执行
            }
        }



        // 获取真实的客户端IP地址
        private IPAddress GetClientIpAddress(HttpContext context)
        {
            // 先检查X-Forwarded-For头
            var xForwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
            if (!string.IsNullOrEmpty(xForwardedFor))
            {
                // X-Forwarded-For可能包含多个IP地址，取第一个
                var firstIp = xForwardedFor.Split(',', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
                if (IPAddress.TryParse(firstIp, out var ip1))
                    return ip1;
            }

            // 检查X-Real-IP头
            var xRealIp = context.Request.Headers["X-Real-IP"].FirstOrDefault();
            if (!string.IsNullOrEmpty(xRealIp) && IPAddress.TryParse(xRealIp, out var ip))
                return ip;

            // 如果没有转发头，使用默认的RemoteIpAddress
            return context.Connection.RemoteIpAddress;
        }


        private bool IsLocalOrLanIp(IPAddress ipAddress)
        {
            if (ipAddress == null)
                return false;


            // 1. 本机IP（127.0.0.1 或 ::1）
            if (IPAddress.IsLoopback(ipAddress))
                return true;

            // 2. 转换为 IPv4 地址（避免 IPv6 兼容问题）
            if (ipAddress.AddressFamily != AddressFamily.InterNetwork)
            {
                // 若为 IPv6，尝试转换为 IPv4（部分场景下的兼容处理）
                var mappedIp = ipAddress.MapToIPv4();
                if (mappedIp == null)
                    return false;
                ipAddress = mappedIp;
            }

            // 3. 局域网IP段判断（常见私有IP段）
            var ipBytes = ipAddress.GetAddressBytes();

            switch (ipBytes[0])
            {
                // 10.0.0.0/8 段（私有IP）
                case 10:
                    return true;
                // 172.16.0.0/12 段（私有IP，范围 172.16.0.0 - 172.31.255.255）
                case 172:
                    return ipBytes[1] >= 16 && ipBytes[1] <= 31;
                // 192.168.0.0/16 段（私有IP）
                case 192:
                    return ipBytes[1] == 168;
                // 其他IP段（非局域网）
                default:
                    return false;
            }
        }


        /// <summary>
        /// 从请求头获取Bearer令牌
        /// </summary>
        private string GetTokenFromHeader(HttpContext context)
        {
            var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
            if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer "))
            {
                return null;
            }
            return authHeader.Split(" ")[1].Trim();
        }
    }

    public interface IJwtService
    {
        string GenerateToken( string username);
        Task<Dictionary<string, object>> ValidateToken(string token);
    }

    public class JwtService : IJwtService
    {
        // 密钥（生产环境需妥善保管）
        private  string SecretKey = Program.Config.KVPairs["JWTSecretKey"];

        /// <summary>
        /// 生成JWT令牌
        /// </summary>
        public string GenerateToken(string username)
        {
            // 令牌过期时间 60分钟

            var token = JwtBuilder.Create()
                      .WithAlgorithm(new HMACSHA256Algorithm())
                      .WithSecret(SecretKey) // 传入密钥
                      .AddClaim("exp", DateTimeOffset.UtcNow.AddMinutes(60).ToUnixTimeSeconds())
                      .AddClaim("name", username)
                      .Encode();

            return token;
        }

        /// <summary>
        /// 验证并解析JWT令牌
        /// </summary>
        public async Task<Dictionary<string, object>> ValidateToken(string token)
        {
            try
            {
                var json = JwtBuilder.Create()
                      .WithAlgorithm(new HMACSHA256Algorithm())
                      .WithSecret(SecretKey) // 验证时也需要相同的密钥
                      .MustVerifySignature()
                      .Decode(token);

                var payload = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
                var userName = payload["name"].ToString();


                if (!(await RedisHelper.ExistsAsync($"admin.{userName}")))
                    throw new Exception("token不存在于redis");

                return payload;
            }
            catch (TokenExpiredException)
            {
                throw new Exception("令牌已过期");
            }
            catch (SignatureVerificationException)
            {
                throw new Exception("签名验证失败");
            }
            catch (Exception ex)
            {
                throw new Exception($"令牌无效：{ex.Message}");
            }
        }

      
    }
}
