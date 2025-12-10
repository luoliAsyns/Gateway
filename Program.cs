
using GatewayService.MiddleWares;
using GatewayService.User;
using LuoliCommon;
using LuoliCommon.Enums;
using LuoliCommon.Logger;
using LuoliHelper.Utils;
using LuoliUtils;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using RabbitMQ.Client;
using System.Data;
using System.Reflection;
using ThirdApis;
using ThirdApis.Services.ConsumeInfo;
using ThirdApis.Services.Coupon;
using ThirdApis.Services.ExternalOrder;
using ILogger = LuoliCommon.Logger.ILogger;

namespace GatewayService
{
    public class Program
    {
        public static Config Config { get; set; }
        private static RabbitMQConnection RabbitMQConnection { get; set; }
        private static RedisConnection RedisConnection { get; set; }

        public static List<string> NotifyUsers;
        private static bool init()
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            bool result = false;
            string configFolder = "/app/Gateway/configs";

#if DEBUG
            configFolder = "debugConfigs";
#endif

            ActionsOperator.TryCatchAction(() =>
            {
                Config = new Config($"{configFolder}/sys.json");

                RabbitMQConnection = new RabbitMQConnection($"{configFolder}/rabbitmq.json");
                RedisConnection = new RedisConnection($"{configFolder}/redis.json");

                var rds = new CSRedis.CSRedisClient($"{RedisConnection.Host}:{RedisConnection.Port},password={RedisConnection.Password},defaultDatabase={RedisConnection.DatabaseId}");
                RedisHelper.Initialization(rds);

                NotifyUsers = RedisHelper.SMembers(RedisKeys.NotifyUsers).ToList();

                //SqlClient.DbFirst.IsCreateAttribute().StringNullable().CreateClassFile(@"E:\Code\repos\LuoliHelper\DBModels", "LuoliHelper.DBModels");

                result = true;
            });



            return result;
        }

           


        public static void Main(string[] args)
        {
            #region luoli code

            Environment.CurrentDirectory = AppContext.BaseDirectory;

            if (!init())
            {
                throw new Exception("initial failed; cannot start");
            }

            #endregion


            var builder = WebApplication.CreateBuilder(args);

            // 配置 Kestrel 支持 HTTP/2
            builder.WebHost.ConfigureKestrel(serverOptions =>
            {
                int port = int.Parse(Config.KVPairs["BindPort"]);

                serverOptions.ListenAnyIP(port, options => {
                    options.Protocols = HttpProtocols.Http1AndHttp2;
                });
            });


            // Add services to the container.

            builder.Services.AddControllers();
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddScoped<AsynsApis>(provider =>
            {
                ILogger logger = provider.GetRequiredService<ILogger>();
                return new AsynsApis(logger, Config.KVPairs["AsynsApiUrl"]);
               
            });
            builder.Services.AddScoped<AgisoApis>();

            builder.Services.AddScoped<SexyteaApis>();

            builder.Services.AddScoped<IExternalOrderRepository, ExternalOrderRepository>();
            builder.Services.AddScoped<ICouponRepository, CouponRepository>();
            builder.Services.AddScoped<IConsumeInfoRepository, ConsumeInfoRepository>();
            builder.Services.AddScoped<IUserRepository, UserRepository>();

            builder.Services.AddScoped<IJwtService, JwtService>();

            builder.Services.AddSingleton<SexyteaAccRecommend>();

            #region 注册 ILogger

            builder.Services.AddHttpClient("LokiHttpClient")
                .ConfigureHttpClient(client =>
                {
                    // client.DefaultRequestHeaders.Add("X-Custom-Header", "luoli-app");
                });

            // 给默认的Logger添加filter
            builder.Logging.AddFilter(
                "System.Net.Http.HttpClient.LokiHttpClient",
                LogLevel.Warning
            );

            // 添加 luoli的 ILogger   loki logger
            builder.Services.AddSingleton<LuoliCommon.Logger.ILogger, LokiLogger>(provider =>
            {
                var httpClient = provider.GetRequiredService<IHttpClientFactory>()
                    .CreateClient("LokiHttpClient");

                var dict = new Dictionary<string, string>();
                dict["app"] = Config.ServiceName;

                var loki = new LokiLogger(Config.KVPairs["LokiEndPoint"],
                    dict,
                    httpClient);
                loki.AfterLog = (msg) => Console.WriteLine(msg);
                return loki;
            });



            #endregion

            #region 注册 rabbitmq

            builder.Services.AddSingleton<RabbitMQ.Client.IConnectionFactory>(provider =>
            {
                return new ConnectionFactory
                {
                    HostName = RabbitMQConnection.Host,
                    Port = RabbitMQConnection.Port,
                    UserName = RabbitMQConnection.UserId,
                    Password = RabbitMQConnection.UserId,
                    VirtualHost = "/",
                    AutomaticRecoveryEnabled = true,
                    NetworkRecoveryInterval = TimeSpan.FromSeconds(5)
                };
            });

            builder.Services.AddSingleton<IConnection>(provider =>
            {
                var factory = provider.GetRequiredService<RabbitMQ.Client.IConnectionFactory>();
                return factory.CreateConnectionAsync().Result;
            });

            builder.Services.AddSingleton<IChannel>(provider =>
            {
                var connection = provider.GetRequiredService<IConnection>();
                return connection.CreateChannelAsync().Result;
            });

            #endregion


            var app = builder.Build();

            ServiceLocator.Initialize(app.Services);

            #region luoli code

            // 应用启动后，通过服务容器获取 LokiLogger 实例
            using (var scope = app.Services.CreateScope())
            {
                var services = scope.ServiceProvider;
                try
                {
                    // 获取 LokiLogger 实例
                    var lokiLogger = services.GetRequiredService<LuoliCommon.Logger.ILogger>();

                    var assembly = Assembly.GetExecutingAssembly();
                    var fileVersionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly.Location);
                    var fileVersion = fileVersionInfo.FileVersion;

                    // 记录启动日志
                    lokiLogger.Info($"{Config.ServiceName} v{fileVersion} 启动成功");
                    lokiLogger.Debug($"环境:{app.Environment.EnvironmentName},端口：{Config.BindAddr}");

                    lokiLogger.Info($"CurrentDirectory:[{Environment.CurrentDirectory}]");
                    lokiLogger.Info($"Current File Version:[{fileVersion}]");

                    ApiCaller.NotifyAsync($"{Config.ServiceName}.{Config.ServiceId} v{fileVersion} 启动了", NotifyUsers);

                }
                catch (Exception ex)
                {
                    // 启动日志失败时降级输出
                    Console.WriteLine($"启动日志记录失败：{ex.Message}");
                }
            }


            #endregion

            app.UseForwardedHeaders(new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
            });

            //app.UseMiddleware<RateLimitMiddleware>();

            //app.UseMiddleware<JwtMiddleware>();

            app.MapControllers();

            app.Run(Config.BindAddr);
        }
    }
}
