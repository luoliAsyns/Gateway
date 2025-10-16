using System.Net;


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
            var ipAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
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
                _logger.Warn($"有东西访问超频率了 [] [{count}]");

                await RedisHelper.SetAsync(cacheKey, LIMIT, TimeSpan.FromSeconds(WINDOW_SECONDS));
                return;
            }

            // 继续处理请求
            await _next(context);
        }
    }
}
