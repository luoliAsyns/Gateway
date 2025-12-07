using System.Net;
using System.Net.Sockets;


namespace GatewayService.MiddleWares
{
    public class RateLimitMiddleware
    {
        private readonly RequestDelegate _next;
        private int LIMIT = 30;
        private int WINDOW_SECONDS = 10;


        private readonly LuoliCommon.Logger.ILogger _logger;

        public RateLimitMiddleware(RequestDelegate next, LuoliCommon.Logger.ILogger logger)
        {
            _logger = logger;
            if (!RedisHelper.Exists("RateLimit_LIMIT"))
                RedisHelper.Set("RateLimit_LIMIT", 3);

            if (!RedisHelper.Exists("RateLimit_WINDOW_SECONDS"))
                RedisHelper.Set("RateLimit_WINDOW_SECONDS", 1);

            LIMIT = RedisHelper.Get<int>("RateLimit_LIMIT");
            WINDOW_SECONDS = RedisHelper.Get<int>("RateLimit_WINDOW_SECONDS");
            _next = next ?? throw new ArgumentNullException(nameof(next));
        }

        public async Task InvokeAsync(HttpContext context)
        {
            // 获取客户端IP地址
            var ipAddress = context.Request.Headers.TryGetValue("X-Forwarded-For", out var xff) ? xff.ToString().Split(',')[0].Trim() :
                               context.Request.Headers.TryGetValue("X-Real-IP", out var xri) ? xri.ToString() :
                               context.Connection.RemoteIpAddress?.ToString();

            IPAddress ip = GetClientIpAddress(context);

            if (IsLocalOrLanIp(ip))
                await _next(context);

            var cacheKey = $"RateLimit_{ipAddress}";

            // 获取Redis数据库

            // 尝试获取当前计数
            var currentCount = await RedisHelper.GetAsync<int>(cacheKey);
            if (currentCount == 0)
            {
                // 首次访问，设置计数为1并设置过期时间
                await RedisHelper.SetAsync(cacheKey, 1, TimeSpan.FromSeconds(WINDOW_SECONDS));
                await _next(context);
                return;
            }


            // 已存在记录，递增计数
            var count = await RedisHelper.IncrByAsync(cacheKey);
            var expireSec = await RedisHelper.TtlAsync(cacheKey);
            if (expireSec < 0)
                await RedisHelper.ExpireAsync(cacheKey, TimeSpan.FromSeconds(WINDOW_SECONDS));

            // 检查是否超过限制
            if (count > LIMIT)
            {
                context.Response.StatusCode = (int)HttpStatusCode.TooManyRequests;
                await context.Response.WriteAsync("繁忙，请稍后重试。");
                _logger.Warn($"有东西访问超频率了 [{cacheKey}] [{count}]");

                await RedisHelper.SetAsync(cacheKey, LIMIT, TimeSpan.FromSeconds(WINDOW_SECONDS));
                return;
            }

            // 继续处理请求
            await _next(context);
        }

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

    }
}
